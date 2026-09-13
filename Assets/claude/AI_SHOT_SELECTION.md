# AI Shot Selection — Core Architecture (shared by all 3 levels)

This describes the ONE algorithm all three difficulty levels share. Only the *numbers*
fed into it differ per level (see `AI_DIFFICULTY_LEVELS.md`) — there should not be three
separate copies of this logic, one per scene. Build it as a single `SnookerAI`
MonoBehaviour driven by a small difficulty-settings asset (see §6), so `Beginner.unity`,
`Medium.unity`, and `Pro.unity` each just reference a different settings asset.

## 0. NON-NEGOTIABLE: legal-target selection is a correctness bug, not a difficulty knob

**Playtesting found the AI attempting shots on balls it isn't legally allowed to hit at
all** — e.g. going for a colour while reds are still on the table. This is not a
"difficulty" concept and must be **100% correct at every single level, including
Beginner** — real players, however unskilled, always know which ball is on; not knowing
that isn't a skill deficiency, it's a rules violation. Candidate generation in §3 below
MUST filter to only the current legal target (`Red` if any reds remain on the table and
`CurrentTargetState == Red`; the specific nominated/sequence colour if
`CurrentTargetState == Colour`) with zero exceptions, at every difficulty level. Before
tuning anything else, verify this filter is actually being applied — if the AI is hitting
illegal balls, the candidate-generation step is either not filtering by legal target at
all, or something downstream is overriding/bypassing the filtered candidate list. Audit
the actual shipped code against this section specifically; do not assume it already does
this correctly just because it was specified this way originally.

## 1. When the AI acts

- Trigger exactly like a human turn: `GameManager.CurrentPlayerIndex == aiPlayerIndex`,
  not `awaitingPlacement`, not `IsConfirmMode` already active from a stray input.
- If `awaitingPlacement` is true on the AI's turn (opening shot or after a foul), the AI
  also needs a placement decision — see §7.
- Add a short "thinking" delay (e.g. 0.5–1.5s, can scale with shot complexity) purely for
  UX so it doesn't feel instant/robotic — not a functional requirement, just feel.

## 2. Perceiving the table (same data a human effectively has)

- Legal target this shot: read `GameManager.CurrentTargetState` /
  `CurrentTargetColour` exactly as `ColourNominationUI` does — the AI must only consider
  balls it's actually legally allowed to hit, same as the human rules.
- Active balls: `GameManager.GetBalls()` filtered to currently-enabled GameObjects, typed
  via `BallIdentity.Type`.
- Ball/pocket positions: ball transforms + the 6 `PocketTrigger` transforms already in
  the scene (read their positions once at `Start()`, cache them).

## 3. Generating candidate shots

For every legal target ball × every one of the 6 pockets, build one candidate:

1. **Ghost ball position**: `ghostBall = objectBall.position - (pocketDir * ballDiameter)`
   where `pocketDir` is the normalized direction from the object ball to that pocket.
2. **Cut angle**: angle between `(cueBall→ghostBall)` and `(objectBall→pocket)`. Discard
   the candidate if this exceeds ~80–85° (physically not makeable — the same geometric
   limit real players run into).
3. **Line-of-sight checks** (reuse the same sphere-cast approach `Cue.cs` already uses
   for `GenerateAimPrediction`, don't reinvent it):
   - `cueBall → ghostBall` must be clear of every other ball (sphere-cast with ball
     radius) and not blocked by a cushion.
   - `objectBall → pocket` must be clear of every other ball.
   - If either is blocked, discard the candidate — it's not a legitimate pot from here.
4. **Distances**: `cueToGhost` and `objectToPocket` — both feed into scoring next.

This produces a list of *makeable* candidate pots given the current table state. An empty
list (no legal pot exists at all) always forces a safety shot regardless of level (see §5).

### 3.1 Would it actually drop? (added after Play-mode measurement)

A clear line to the pocket's centre is not enough on this table. Each candidate is also checked
the way the ball will really travel:

- **Jaws.** `DropsInto` sweeps the object ball (0.6 × radius, since a ball that only grazes a jaw
  usually still drops) along its line until it reaches the pocket trigger's capture distance.
  Any cushion or jaw collider on the way rejects the pot. Checked against 80 rolled balls: 73
  agreed, and it never said "drops" for a ball that stayed out.
- **The level's own error.** `IsMakeableAtThisSkill` swings the aim by ± a share of the middle of
  the level's aim-error range (`profile.makeabilityErrorFraction`, per-level - not a shared
  constant) and follows each through the exact contact geometry (a cut multiplies the error). It
  adds the same share of the leftover throw uncertainty and requires both sides to still drop and
  stay clear of other balls. Larger errors may not pot, so tight pots are still missed some of the
  time.
  The full midpoint was measured to be far too cautious. Medium dropped 80 of the 81 pots it
  attempted, yet ~85% of its visits ended on "no makeable pot". When it attempted the pots that gate
  rejected, 65 of 91 still went in. Same seed, 60 visits each:

  | Share of the error | Medium pots/visit | Pro pots/visit (5+ visits) | Attempts potted |
  |---|---|---|---|
  | 1.0 (old, shared) | 1.37 | 2.25 (10/60) | 93–98% |
  | 0.5 | 2.40 | — | 92% |
  | 0.25 (shared, first pass) | 2.82 | 3.73 (22/60) | 87–89% |

  Made per-level in the next pass, since a shared value can't let Pro (tiny real aim error, so even
  a loosely-gated pot mostly still drops) run further ahead of Medium than Medium can run ahead of
  Beginner. Loosening Pro's further than Medium's (`0.08` vs staying at `0.15`) was tried first and
  *broke* the pot-rate/quality ordering in an 18-visit check: Pro's own attempt failure rate rose
  from 12% to 22%, closing the gap with Medium instead of extending it. Shipped: Medium `0.2`, Pro
  `0.18` - both looser than the original shared `0.25`, but calibrated so each level's measured
  pots/visit and pot-attempt success rate stay clearly ordered Pro > Medium > Beginner (see
  `AI_DIFFICULTY_LEVELS.md`'s second measured pass).

  Beginner never filters candidates on makeability (it's never `PlaysDeliberateSafety`) so this
  fraction barely affects it; its throughput instead comes from `minAcceptablePotScore`, lowered
  0.6 → 0.35 → 0.15, and `candidateSurveyCount` raised 1 → 2 (still below Medium's 3 and Pro's full
  survey).
- **Next-shot quality** (§4, §5.1) only credits a leave with a pot the level could make by the
  same test. It is averaged over the predicted cue-ball rest and ±30% of its travel, because the
  rest estimate is typically 0.1–2 units out.

## 4. Scoring candidates

```
difficulty = (cutAngle / 85°) * 0.6 + ((cueToGhost + objectToPocket) / tableDiagonal) * 0.4
potScore = 1 - clamp01(difficulty)     // 1 = trivially easy, 0 = barely makeable
```

Levels that use **positional play** (medium/pro, see next doc) add a second term:
simulate/approximate where the cue ball ends up after this pot (see §5.1), then score
how good that leaves the *next* shot (distance + angle to the nearest still-legal ball).
Combine as `finalScore = potScore * potWeight + positionScore * positionWeight`, where
`positionWeight` is 0 for beginner (doesn't plan ahead at all) and increases for
medium/pro (see next doc for exact weights).

## 5. Deciding: attempt a pot, or play safe?

- If candidate list is empty → must play a **safety shot** (no legal pot exists).
- Otherwise, each level has a `safetyProbability` and/or a `minAcceptablePotScore`
  threshold (see next doc). If the best candidate's score is below that threshold, OR a
  random roll falls under `safetyProbability`, the AI plays safety instead of attempting
  the pot — this is what produces *deliberate* snookers/safeties instead of the AI always
  just going for broke.
- **Safety shot construction** (when chosen): aim to strike the legal ball thinly/softly
  such that the cue ball ends up blocked from a straight line to the next legal ball for
  the opponent, ideally behind another ball or tucked near a cushion. Implementation:
  among the legal target balls, pick the contact that leaves the cue ball closest to
  directly behind another non-legal ball relative to the opponent's likely next target,
  using the same line-of-sight sphere-cast from §3 to test candidate resting spots.
  This does not need to be perfect — even an approximate safety (low power, glancing
  contact, cue ball drifts toward a cushion) is enough to read as "playing safe" rather
  than "trying to pot," which is the actual functional requirement.

### 5.1 Approximating cue-ball position after a shot (for positional scoring / safety)

You don't need a full physics simulation here — a cheap approximation is enough:
- Non-screw pot: cue ball roughly continues along the tangent line direction (same
  formula `Cue.cs`'s `GenerateAimPrediction` already computes for `cueDirAfter`), traveling
  a distance loosely proportional to shot power minus the energy transferred into the
  object ball (rule of thumb: thinner cut = cue ball keeps more energy/travels further).
- If the AI deliberately calls for backspin/topspin (medium/pro only, see next doc), bias
  the resulting position backward/forward along the original aim line instead of purely
  tangent — a rough approximation is fine, this is only used to *rank* candidates
  relative to each other, not to guarantee an exact outcome (the real physics engine
  produces the actual result once the shot is struck).

## 6. Executing the chosen shot (through the SAME pipeline a human uses)

Once a shot (pot or safety) is chosen as `(aimForward, spinOffset, powerFraction)`:

1. Apply the level's **error injection** (see next doc) to `aimForward` (small random
   rotation) and `powerFraction` (small random multiplier) — this must happen for every
   single shot, no exceptions, including safeties. This is what makes the AI beatable at
   all levels rather than mechanically perfect.
2. Feed the result into the exact same execution path a human's input produces:
   `GameManager.SetStrikeForce(...)`, set the equivalent of the spin dot's `spinOffset`
   for `Cue.cs`'s offset-impulse strike (per `SPIN_LOGIC.md`), then `RequestStrike()`.
   **Do not** give the AI a separate, more-precise code path that bypasses the normal
   strike pipeline — it should be physically capable of exactly what a human is capable
   of, with worse aim, not literally superhuman precision under the hood.

### 6.1 Planning the strike so the physics does what was planned

Measured in Play mode through the real pipeline, not assumed:

- **Throw compensation.** With zero aim error the object ball leaves 1.4–7.8° off the ghost-ball
  line on cut shots, dragged towards the cue ball's travel. `ExpectedThrowDegrees` is a fit to 26
  calibration strikes (cut angle, impact speed, topspin, sliding vs rolling). The AI aims a touch
  thinner to cancel it, as a human learns to. On 12 check shots it cut the zero-error object-ball
  error from 3.4° to 1.1°. This is planning, not precision: error injection (§6 step 1) still
  applies afterwards.
- **Pot power.** A cut only sends the object ball off at `impact speed × cos(cut)`. Distance-only
  power under-hit cut pots, which stopped 1.3–2.7 units short. `PotPowerFor` works back from
  the object ball reaching the pocket with room to spare, allowing for the level's worst
  under-hit.
- **Pot pace is capped, and is separate from `basePowerFraction`.** Pots are paced only from what
  the object ball needs, not from the safety/escape power mapping. Position play's extra pace (×1.3,
  ×1.6) is capped so the cue ball arrives at no more than 6.5 m/s, unless the pot genuinely needs
  more. Measured on 137 logged pots: under 6 m/s none failed and the object ball left within a median
  0.4–0.8° of the plan. Above 11 m/s the median launch error was 3.6° and 14 of 52 failed, with no ball
  touched first. The collision itself stops being predictable at that pace. (Pro's scene briefly had
  `basePowerFraction` 0.8, which drove every pot to that speed: 27% of pot attempts failed, against
  8% with spec power and the cap.)
- **What `basePowerFraction` controls now.** Only safeties (`SafetyPowerFor`, including Beginner's
  soft contact). Pots use `PotPowerFor`, escapes a fixed 0.3, and the break `breakPowerFraction`.
  Measured straight shots through the real strike pipeline, as power → launch speed → total roll:
  0.10 → 2.7 m/s → 3.6 units, 0.15 → 3.7 → 6.3, 0.25 → 5.6 → 13.8, 0.40 → 8.4 → 20.8,
  1.0 → 19.6 → 65.8 (table diagonal 16.6). At spec power (0.10) safeties went out around 0.12,
  and felt too weak in play even though only 4 of 115 logged safeties actually failed to reach the
  ball. Raising it to 1.0 sent safeties out at ~0.69 and fouled 11 of 22 - but that was almost
  entirely the missed-colour foul bug (below), not the extra pace: with that bug fixed and
  `basePowerFraction` shipped at 0.85 (safeties out around 0.7), an 18-visit check across all three
  levels logged 16 safety attempts and 2 fouls (12.5%).
- **Sight margin near clusters.** `sightMarginBallRadii` stays at 0.25. With the clearance logging
  below, no run showed misses concentrating on shots with a tight gap beside the line, and no failure
  started with the object ball clipping a neighbour. The AI hardly ever takes a pot on a red inside a
  cluster, because the existing line checks already reject those. Raising the margin to 0.75 on Pro gave
  3/69 failures against 6/75, but the misses it removed were not tight-gap shots, and pots per visit
  fell slightly (0.89 → 0.84).
- **Cue-ball rest** (§5.1) is fitted to 18 real strikes rather than a guess: the old estimate put
  a 60° cut's cue ball 0.9 units away when it travelled 4.6.
- **Side spin is not used on pots yet.** Side spin bends the cue ball's path (squirt/swerve), and
  nothing models that. On Pro, pots played with side spin missed 19 of 27, sending the object ball
  3–41° off, against 2 of 67 without it. Until that is measured and allowed for, pots only use
  follow/stun/screw. Pro's "full" spin usage is therefore limited to vertical spin for now.
- **Levels that never play safe** (Beginner, `safetyProbability 0`) are not filtered by
  makeability. They take a makeable pot when one exists, and otherwise still go for the easiest
  pot they see and miss naturally, as §5 and the Beginner row in `AI_DIFFICULTY_LEVELS.md` describe.
- **Debug tracking.** With `debugLogging` on, every pot attempt logs the predicted vs actual
  object-ball direction, how close it got to the pocket, and how far the cue ball stopped from
  the predicted rest. It also logs how much room each shot line had (`gaps cue->ghost=… obj->pocket=…`,
  the gap left beside the travelling ball by the nearest other ball, in ball radii), how many balls sit
  within three ball widths of the object ball (`cluster3D`), and which ball, if any, the cue ball or
  object ball actually ran into (`contacts: …`). That separates a secondary collision from a bad
  contact. The "actual objDev" is read two physics steps after the object ball starts moving. A large
  value with no contact listed was already off at launch, not knocked off by a neighbour.

## 7. Ball-in-hand placement (opening shot / after a foul)

When `awaitingPlacement` is true on the AI's turn: pick a placement point inside the D
(reuse the same geometry from `BALL_PLACEMENT_D.md` §3) that gives the AI the best
opening candidate shot per §3–4 — i.e., run the same candidate-generation step for a few
sample points inside the D and pick whichever gives the highest-scoring legal shot. No
special-casing needed; it's the same shot-evaluation logic, just choosing where to start
from as well as where to aim.

## 8. Turn loop

After the AI's shot resolves (`OnAllBallsStopped` → `EvaluateFoul`/`EvaluateShotResult`,
identical to a human turn — the AI doesn't get different rules), if it's still the AI's
turn (pot was legal, `PassTurn()` wasn't called), repeat from §1 for the next shot,
factoring in the **pressure/soft-cap system** from the next doc, which is what limits how
many balls the AI potentially runs in one visit.