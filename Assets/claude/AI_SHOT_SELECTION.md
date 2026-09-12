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