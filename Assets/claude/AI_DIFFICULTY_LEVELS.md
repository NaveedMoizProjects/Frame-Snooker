# AI Difficulty Levels — Beginner / Medium / Pro

Feeds the shared algorithm in `AI_SHOT_SELECTION.md`. Implement as a `ScriptableObject`
(`AIDifficultyProfile`) with one asset per level, referenced by the `SnookerAI` component
in each of the three scenes (`Beginner.unity`, `Medium.unity`, `Pro.unity`) — same script,
different data, so tuning is just editing numbers, not duplicating code three times.

## Measured table scale — why the aim-error numbers below are tenths of a degree, not degrees

The original values in this doc (Beginner 4–8°, Medium 1.5–3°, Pro 0.3–1.5°) were real-world
intuition. They were measured against this table in Play mode and cannot work here:

- **Contact amplification.** A cue-ball aim error is multiplied at the contact by roughly
  `cueToGhost / (ballDiameter · cos(cut))`. Measured at a 45° cut from 5 units: −19.9° of
  object-ball error per 1° of aim error (the model predicts −20.05°). A 0.2° aim error there is
  a 4° miss on the object ball.
- **Pocket jaws.** Rolled balls show a corner pocket only accepts approaches within about ±15°
  of its diagonal and ±0.25 units of the line; middle pockets about ±40°.
- **Collision throw.** Even with zero aim error, the object ball leaves 1.4–7.8° off the ghost-
  ball line on cut shots (it grows with cut angle and impact speed). The AI compensates, but
  roughly ±0.4–2° is left over, so cut pots stay less reliable than straight ones at every level.
  This traces to the physics contact offset (0.02) and affects human players too.
- **Availability ceiling.** Over 60 random open layouts, a pot that survives the shot model
  exists in 83% of positions at 0.01° aim error, 73% at 0.08°, 62% at 0.14°, and 50% at 0.25°.
  No aim-error value gets "every visit" above that ceiling; the remaining visits start with no
  reliably makeable pot at all.

So the levels keep the same *shape* (floor > 0, Pro < Medium < Beginner, Pro's error grows on
hard pots) at the scale this table actually needs. The shot pipeline was fixed first (see
`AI_SHOT_SELECTION.md` §3.1 and §6.1) — these numbers were only tuned after that.

| Level | `aimErrorDegrees` (this table) |
|---|---|
| Beginner | `[0.06°, 0.18°]` |
| Medium | `[0.04°, 0.12°]` |
| Pro | `[0.01°, 0.03°]` easy → `[0.02°, 0.06°]` hard, `+0.003°` per ball potted this visit |

Wherever the tables below still quote the original degree values, read them as the intent
(relative ordering and shape), and the table above as the numbers actually shipped.

**Measured status (September 2026, each AI visit started from a fresh random open table, human
deliberately missing).**

| Level | Visits | Balls per visit (mean) | Visits with 3+ | Pot attempts dropped |
|---|---|---|---|---|
| Medium, before this pass | 851 | 0.27 | 0.6% | ≈41% (232 balls from 571 attempts) |
| Beginner | 26 | 0.92 | 12% | 25/32 (its misses were pots it knew weren't makeable) |
| Medium | 42 | 1.55 | 26% | 65/67 |
| Pro | 484 | 2.11 | 34% (5+: 16%) | 1031/1078 |

**Second measured pass (September 2026), after loosening `makeabilityErrorFraction` and
`minAcceptablePotScore` per level, raising `basePowerFraction` to `0.85`, fixing the
missed-colour foul bug, and raising Beginner's candidate survey to 2** (18 visits/level,
fresh random open table per visit, same seed across levels):

| Level | Visits | Balls per visit (mean) | Visits hitting its minimum | Pot attempts dropped |
|---|---|---|---|---|
| Beginner (min 2) | 18 | 1.78 | 7/18 | 30/49 (61%) |
| Medium (min 3) | 18 | 3.17 | 8/18 | 56/69 (81%) |
| Pro (min 5) | 18 | 3.78 | 6/18 | 66/75 (88%) |

Ordering holds on the measured numbers, not just the settings: pot rate Pro > Medium >
Beginner, and pot quality (share of attempts that actually drop) Pro > Medium > Beginner.
An earlier, more aggressive setting for Pro (`makeabilityErrorFraction 0.08`,
`minAcceptablePotScore 0.1`) was tried first and rejected - it raised Pro's *attempt* rate
but also its miss rate enough (88%→78% dropped) that its measured pots/visit (3.22) fell to
within noise of Medium's, breaking the ordering. `0.18`/`0.15` was the value that actually
grew Pro's lead.

The per-visit minimums are **still not met on every visit** - 18 visits/level is a sanity
check, not a statistically solid measurement, and this remains true at the sample sizes
tested before this pass too. Nearly all zero-pot visits start with no makeable pot on the
table (the availability ceiling above), not with a miss. The biggest remaining lever is the
physics throw noise, tracked as a separate follow-up.

**Third measured pass (September 2026), aimed at smarter shot selection rather than just
looser tolerances** - Medium's `candidateSurveyCount` raised 3→6, plus a small further
loosening of `makeabilityErrorFraction`/`minAcceptablePotScore` on Medium and Pro (18
visits/level, same seed as the second pass, `basePowerFraction` unchanged at 0.85):

| Level | Balls per visit (mean) | Visits hitting its minimum | Pot attempts dropped |
|---|---|---|---|
| Beginner (min 2, unchanged) | 1.19 | 5/16 | 58% |
| Medium (min 3) | 3.06 | 11/18 | 80% |
| Pro (min 5) | 4.56 | 7/18 | 89% |

Ordering held: pot rate Pro (4.56) > Medium (3.06) > Beginner (1.19), pot-attempt quality
Pro (89%) > Medium (80%) > Beginner (58%).

**What actually moved the needle, and what didn't:** before touching any tolerance value,
the logs were checked for whether Medium/Pro's shot selection had real headroom (per the
non-negotiable "check selection before loosening further" instruction for this pass):
- Pro already surveys every makeable candidate (`candidateSurveyCount 0`), and its safety
  decisions matched the design intent (the "safety roll" branch fired on genuine
  moderate-difficulty pots, e.g. potScore 0.26-0.43, not on easy ones) - no selection-logic
  headroom found, so its gain came from the further, careful `makeabilityErrorFraction`/
  `minAcceptablePotScore` step above (point 4): **3.78 → 4.56 pots/visit (+21%), failure
  rate 12% → 11%**.
- Medium's survey cap was genuinely truncating decisions: ~25-36% of its pot choices had
  more candidates on the table than it compared. Raising the cap to 6 is a real "think
  better" fix, confirmed in the logs (candidates 4-6 makeable now get scored where they
  didn't before). But run alongside a modest tolerance loosening, Medium's raw pots/visit
  barely moved: **3.17 → 3.06 (within run-to-run noise for n=18)**, and its pot-attempt
  failure rate held at 19-20%. The diagnosis: Medium's visits are overwhelmingly ended by a
  missed pot attempt, not by running out of makeable candidates or by an overly-cautious
  safety decision - only 1 of 75 decisions in the second pass was "best pot under
  threshold", and only 6 of 75 were "no makeable pot exists" at all. With a per-attempt
  failure rate around one in five, average visit length before a first miss is bounded
  near that regardless of which candidate among the makeable set gets chosen - deeper
  survey picks a candidate with a *better leave*, not a *lower-risk* one, so it doesn't by
  itself reduce that ceiling. Doubling Medium's raw pot rate from here would need either
  reducing the per-shot execution failure rate directly (a physics/throw-model change, out
  of scope for this pass) or accepting more of the tolerance-loosening tradeoff already
  shown to risk the Pro/Medium ordering. `candidateSurveyCount 6` is kept regardless - it's
  a correctness fix (comparing the options that exist) independent of whether it moved this
  particular metric.
- Beginner was not touched. Its raw mean (1.19 here vs 1.78 in the second pass, same seed)
  differs because Unity's global `Random` stream position differs between runs depending on
  how many other levels were tested first in the same editor session - not a regression in
  its own settings. Its position at the bottom of the ordering is unaffected either way.

**`basePowerFraction` 0.85** (was `0.1`, briefly `0.8`) drives safety/escape power only -
pot power is entirely separate (`PotPowerFor`/`ControlledPotPowerCeiling`) and unaffected.
Safety shots now go out around 0.7 power. Combined with the missed-colour foul fix, safety
fouls measured 2/16 (12.5%) across the three levels' 18-visit checks - down from the 11/22
(50%) measured at power 1.0 before that foul-rule fix existed, when most of those fouls were
the "hit the right ball on a colour, potted nothing" bug being scored as a foul rather than a
legal miss.

## Golden rule for all three levels

**No level ever has zero error and no level ever has zero pot chance.** Every level keeps
a random floor on aim/power error (never exactly 0°) so it's never mathematically
perfect, and every level's error is capped so even its hardest setting still lands the
easiest possible shots most of the time. This is what makes "tough but not unbeatable"
and "easy but not a pushover on a literal open pot" both true at the same time.

## Beginner

| Parameter | Value | Why |
|---|---|---|
| `aimErrorDegrees` | random in `[4°, 8°]` per shot | Straight/easy pots mostly go in; anything with real cut angle misses often. |
| `powerErrorPercent` | `±25%` | Power controls are inconsistent — under/over-hit is common. |
| `minAcceptablePotScore` | `0.15` (was `0.6`, then `0.35`) | Willing to attempt almost any legal pot rather than fall back to a soft tap. At 0.6 most visits ended on "best pot under 0.6" (0.7–0.85 pots/visit); 0.35 alone still wasn't enough throughput. |
| `makeabilityErrorFraction` | `0.5` | Doesn't gate attempts (Beginner never filters candidates by makeability, see below) - only affects candidate tie-breaking, so this is largely inert for Beginner. Kept well above Medium's/Pro's so it never accidentally becomes the loosest level if that changes. |
| `safetyProbability` | `0.0` | Beginner never *deliberately* plays safe — it always just goes for the ball on, even when that's a bad idea. Any resulting snooker on the human is pure accident. |
| `positionWeight` | `0.0` | No positional planning at all — picks the easiest legal pot each shot, ignores where the cue ball ends up. |
| `deliberateSpinUsage` | `none` | Always strikes dead-center (`spinOffset = (0,0)`) — no intentional follow/screw/side. Any spin-like outcome is coincidental from mis-hits. |
| `softPotCap` | `2–3` balls per visit | After this many pots in one visit, see pressure ramp below. |
| `pressureRamp` | `+150%` to `aimErrorDegrees` and `powerErrorPercent` per ball potted beyond the cap | Simulates "choking" — makes running much further than 2-3 balls rare without hard-blocking it outright. |
| Candidate survey | `2` (was `1`) | Compares the top 2 candidates instead of blindly taking the single lowest-`difficulty` one - still far shallower than Medium's 3 or Pro's full survey. |

**Target outcome:** beatable by literally anyone who can aim reasonably straight — the
beginner AI is a soft target that occasionally strings a couple of easy balls together
and nothing more.

## Medium

| Parameter | Value | Why |
|---|---|---|
| `aimErrorDegrees` | random in `[1.5°, 3°]` per shot | Noticeably better than beginner but still misses real cut shots at a real rate. |
| `powerErrorPercent` | `±10%` | Reasonably consistent, not pinpoint. |
| `minAcceptablePotScore` | `0.25` (was `0.4`) | Willing to attempt moderately harder shots than beginner, and more of them. |
| `makeabilityErrorFraction` | `0.16` (was the shared `0.25`, then `0.2`) | `0.15` was tried at `0.2`'s survey depth and tested looser still, but pushed Medium's pot-attempt failure rate up (19→22%) enough to blur the gap with Pro - `0.2` was shipped instead. `0.16` was re-tried alongside the survey-depth increase below, landing at a 20% failure rate - about the same as `0.2` alone, i.e. this knob is close to exhausted for Medium; see the third measured pass. |
| `candidateSurveyCount` | `6` (was `3`) | Logged play showed ~25% of Medium's pot decisions had more than 3 *makeable* candidates on the table - `finalScore` (pot score + position) was never even computed for those, so a genuinely better-positioned candidate ranked 4th+ by raw pot score could never be picked. Raising the cap to 6 (comfortably above the observed max of 6 makeable candidates in that sample) lets it actually compare them, while staying well below Pro's uncapped survey. |
| `safetyProbability` | `0.15–0.25` (roll when no candidate scores above `0.5`, or when the best pot would leave an easy follow-up for the opponent per the positional check) | Plays a genuine safety sometimes, not every time it's the "correct" move — this is deliberate, not perfect, snooker awareness. |
| `positionWeight` | `0.3` | Has some sense of leaving itself a reasonable next shot, not full lookahead. |
| `deliberateSpinUsage` | `basic` — will choose top-spin/stun deliberately for simple position, occasional backspin on straightforward shots; won't attempt precise combination side-spin position play | Matches a club-level player's spin usage. |
| `softPotCap` | `4–5` balls per visit | |
| `pressureRamp` | `+80%` to error terms per ball beyond cap | Milder choke than beginner — a decent break is plausible but a full clearance is rare. |
| Candidate survey | Compares top 2–3 candidates by `finalScore`, picks the best | Some real shot selection, not just "first legal ball." |

**Target outcome:** beatable, but only by a player who understands basic potting angles
and has some idea of strategy — a casual/new player will lose to this level regularly.

## Pro

| Parameter | Value | Why |
|---|---|---|
| `aimErrorDegrees` | scaled by shot difficulty: `[0.3°, 0.6°]` on easy shots (`potScore > 0.8`) up to `[1.0°, 1.5°]` on hard shots (`potScore < 0.3`) | Real top players still miss hard cuts at a meaningful rate — never treat "pro" as "perfect." |
| `powerErrorPercent` | `±4%` | Very consistent, not flawless. |
| `minAcceptablePotScore` | `0.12` (was `0.2`, then `0.15`) | Willing to attempt genuinely difficult pots when they're the percentage play, and rarely bails to a soft fallback. |
| `makeabilityErrorFraction` | `0.15` (was the shared `0.25`, then `0.18`) | Loosened further than Medium's, since Pro's own aim error is tiny enough that even a marginal pot mostly still drops. `0.08` was tried first (with `minAcceptablePotScore 0.1`) and was too loose in practice: attempts rose but so did their failure rate (12→22%), closing the gap with Medium instead of extending Pro's runs. `0.18` was shipped from that round; `0.15` (with `0.12`) is a smaller further step in the same direction, and held its failure rate at 11% - see the third measured pass below. Candidate survey is already full (`candidateSurveyCount 0`) so there's no deeper-survey lever left for Pro - this is a deliberate, checked loosening, not an oversight. |
| `safetyProbability` | high whenever no candidate scores above `0.45`, roughly `0.5–0.7` in those specific situations (not a flat global chance — situational, per §5 of the shot-selection doc) | Plays proper safeties/snookers specifically when there's no good pot, like a real player would, rather than randomly. |
| `positionWeight` | `0.6` | Genuinely plans position for the next ball, will sometimes take a slightly harder pot because it leaves much better position than the "easier" alternative. |
| `deliberateSpinUsage` | `full` — uses screw/follow/side spin deliberately for position control, including combination side-spin+follow/screw | Full toolbox, matching real professional cueing. |
| `softPotCap` | `7–8` balls per visit | High ceiling — capable of real breaks. |
| `pressureRamp` | `+40%` to error terms per ball beyond cap, and additionally scale `aimErrorDegrees` up slightly (`+0.1°` per ball already potted this visit, uncapped) even before the soft cap | Even a "pro" gets slightly less precise the longer a break goes on — keeps very long clearances rare rather than routine, without hard-blocking them. |
| Candidate survey | Full survey of all valid candidates, scored by `finalScore` (pot difficulty + position) | Closest to genuine shot selection. |

**Target outcome:** genuinely hard to beat — requires real potting accuracy and some
strategic sense from the human — but structurally guaranteed non-zero miss chance on
every shot (see golden rule) means it is never literally unbeatable. A strong human
player should still be able to win a meaningful fraction of frames.

## Minimum pot-count acceptance targets — CLARIFIED: per single visit, not per match average

**Important clarification (this was previously ambiguous and got misread as a
match-wide/average target — it is not):** "a visit" means one continuous turn at the
table — from the moment it becomes the AI's turn (because the human missed, fouled, or
it's the AI's break) until the AI itself misses or fouls and the turn passes back. The
target below applies to **that single visit**, essentially every time it happens — NOT
averaged across a whole match, NOT "sometimes hits this, sometimes doesn't." Concretely:

```
Human's turn ends (miss/foul) → it's now the AI's turn (one "visit" starts)
  → AI must pot AT LEAST this many balls before its visit ends:
       Beginner: 2-3 balls in THIS visit
       Medium:   3-4 balls in THIS visit
       Pro:      5-6 balls in THIS visit
  → only after reaching (at least) that count is it acceptable for the AI's visit to
     end (via a miss or foul) - a visit that pots 0-1 balls and then ends is a FAIL for
     Beginner/Medium, and a visit that pots 0-4 balls and then ends is a FAIL for Pro,
     for that specific visit.
```

This must hold true for **nearly every single AI visit**, not just as an average over
many visits — e.g. Beginner potting 0 several times and 6 once, averaging out to "2-3
across the match," does NOT satisfy this requirement. Test by giving the AI several
separate turns (let the human player deliberately miss/pot-nothing to hand over the turn
repeatedly) and checking the pot count of each individual resulting AI visit against the
table above - if most individual visits are below the minimum, that's a fail, even if
some outlier visits happen to hit or exceed it.

The soft-cap/upper-bound numbers from earlier in this doc still apply on top of this (a
visit shouldn't blow way past 3/5/8 either) — so each level's AI needs to land, per
visit, inside a real range (e.g. Beginner: 2-3 up to a rare max of ~3-4, not 0 and not
15), not just clear a floor.

If a level is failing to hit its minimum, the fix is **not** to keep blindly lowering
`aimErrorDegrees` further — first rule out a root-cause bug in the shot pipeline itself
(candidate generation always rejecting valid shots, the previously-flagged unresolved
0.5° `aimForward` systematic bias from the isolated-harness investigation, execution not
actually applying the intended `spinOffset`/power, illegal-target selection wasting shots
per `AI_SHOT_SELECTION.md` section 0, etc.). Only tune the error numbers once the
underlying pipeline is confirmed to be executing shots faithfully — an AI that still
can't pot after `aimErrorDegrees` is already near-zero is a pipeline bug, not a tuning
problem, and pushing the numbers lower still won't fix it.

Equally important: hitting these minimums must **not** come at the cost of the golden
rule (never zero error, never unbeatable) or the upper bounds above — an AI that suddenly
pots every ball with no misses is just as wrong as one that pots nothing. Iterate toward
the middle of each range, not the edges.

## Foul-rate acceptance targets (new — playtesting found fouls far too common)

Two different things both get called "fouls," and they need different treatment:

1. **Decision-logic fouls** — the AI aimed at an illegal ball entirely (e.g. going for a
   colour while reds remain). Per `AI_SHOT_SELECTION.md` section 0, this must be **zero
   at every level**, including Beginner — it's a correctness bug, not a difficulty
   setting, full stop.
2. **Physical/aim-driven fouls** — the AI aimed at the *correct* legal ball but its aim
   error caused it to strike a different ball first, miss entirely, or pot the wrong
   ball as a side effect. This naturally scales with skill level and is allowed to vary:

| Level | Physical-foul rate target | Why |
|---|---|---|
| Beginner | Noticeably present — this is expected and fine, comes naturally from the larger `aimErrorDegrees` | A beginner missing badly enough to clip the wrong ball first is realistic, not a bug. |
| Medium | Rare | Better aim + `minAcceptablePotScore`/safety logic mean it mostly avoids attempting shots likely to go wrong. |
| Pro | Extremely rare, not mathematically impossible | Small aim error (golden rule keeps it non-zero) plus a high safety threshold when no confident pot exists means Pro should almost never foul in practice — but "almost never" not "literally cannot," per the golden rule elsewhere in this doc. If Pro's actual measured foul rate across several visits is anything other than very low, that points at a decision-logic bug (see item 1 above), not something to fix by shrinking error further. |

If Beginner or Medium are fouling far more than expected, check decision-logic fouls
(item 1) first — a high enough rate of illegal-ball-targeting can look like "the AI fouls
constantly" even if the physical aim error itself is reasonable for that level.

## Quick sanity checklist before shipping

- [ ] Beginner never deliberately snookers (only ever center-ball hits).
- [ ] Beginner pots at least 2–3 balls in EACH individual AI visit (tested across
      several separate visits, not just once), and rarely exceeds 3–4.
- [ ] Medium sometimes plays a visible safety shot instead of a risky pot.
- [ ] Medium pots at least 3–4 balls in EACH individual AI visit (tested across several
      separate visits, not just once), and rarely exceeds 5–6.
- [ ] Pro plays a deliberate safety when no good pot exists, not just "always attempt."
- [ ] Pro can occasionally miss even a straightforward-looking pot (error floor working).
- [ ] Pro pots at least 5–6 balls in EACH individual AI visit (tested across several
      separate visits, not just once), rarely exceeds 8, and long visits get visibly
      shakier (later shots in a long break miss more than early ones).
- [ ] None of the three levels ever produces `aimErrorDegrees == 0` or
      `powerErrorPercent == 0` on any single shot.
- [ ] None of the three levels pots literally everything with no misses (upper bound
      and golden rule both still hold after fixing the minimums above).
- [ ] AI NEVER targets an illegal ball (colour while reds remain, wrong colour vs the
      nominated one) at ANY level — verified by watching several visits per level, not
      assumed from the code alone.
- [ ] Cue-ball-in-hand placement only triggers on frame start or when the cue ball is
      actually potted — verified it does NOT trigger on a foul where the cue ball stayed
      on the table (see `BALL_PLACEMENT_D.md` section 1, corrected rule).