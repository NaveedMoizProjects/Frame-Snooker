# AI Difficulty Levels — Beginner / Medium / Pro

Feeds the shared algorithm in `AI_SHOT_SELECTION.md`. Implement as a `ScriptableObject`
(`AIDifficultyProfile`) with one asset per level, referenced by the `SnookerAI` component
in each of the three scenes (`Beginner.unity`, `Medium.unity`, `Pro.unity`) — same script,
different data, so tuning is just editing numbers, not duplicating code three times.

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
| `minAcceptablePotScore` | `0.6` | Only attempts the fairly easy pots it can actually see; won't blindly go for a 1%-chance thin cut. |
| `safetyProbability` | `0.0` | Beginner never *deliberately* plays safe — it always just goes for the ball on, even when that's a bad idea. Any resulting snooker on the human is pure accident. |
| `positionWeight` | `0.0` | No positional planning at all — picks the single easiest legal pot each shot, ignores where the cue ball ends up. |
| `deliberateSpinUsage` | `none` | Always strikes dead-center (`spinOffset = (0,0)`) — no intentional follow/screw/side. Any spin-like outcome is coincidental from mis-hits. |
| `softPotCap` | `2–3` balls per visit | After this many pots in one visit, see pressure ramp below. |
| `pressureRamp` | `+150%` to `aimErrorDegrees` and `powerErrorPercent` per ball potted beyond the cap | Simulates "choking" — makes running much further than 2-3 balls rare without hard-blocking it outright. |
| Candidate survey | Only evaluates the single lowest-`difficulty` candidate, doesn't compare alternatives | Matches "just goes for the obvious ball" beginner behavior. |

**Target outcome:** beatable by literally anyone who can aim reasonably straight — the
beginner AI is a soft target that occasionally strings a couple of easy balls together
and nothing more.

## Medium

| Parameter | Value | Why |
|---|---|---|
| `aimErrorDegrees` | random in `[1.5°, 3°]` per shot | Noticeably better than beginner but still misses real cut shots at a real rate. |
| `powerErrorPercent` | `±10%` | Reasonably consistent, not pinpoint. |
| `minAcceptablePotScore` | `0.4` | Willing to attempt moderately harder shots than beginner. |
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
| `minAcceptablePotScore` | `0.2` | Willing to attempt genuinely difficult pots when they're the percentage play. |
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

## Quick sanity checklist before shipping

- [ ] Beginner never deliberately snookers (only ever center-ball hits).
- [ ] Beginner's pot count in a typical visit rarely exceeds 3.
- [ ] Medium sometimes plays a visible safety shot instead of a risky pot.
- [ ] Medium's pot count in a typical visit rarely exceeds 5.
- [ ] Pro plays a deliberate safety when no good pot exists, not just "always attempt."
- [ ] Pro can occasionally miss even a straightforward-looking pot (error floor working).
- [ ] Pro's pot count in a typical visit rarely exceeds 8, and long visits get visibly
      shakier (later shots in a long break miss more than early ones).
- [ ] None of the three levels ever produces `aimErrorDegrees == 0` or
      `powerErrorPercent == 0` on any single shot.
