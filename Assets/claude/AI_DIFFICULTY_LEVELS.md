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
| `minAcceptablePotScore` | `0.15` (shipped; `0.6` in the original spec) | Only attempts the fairly easy pots it can actually see; won't blindly go for a 1%-chance thin cut. Lowered across several tuning passes once measurement showed 0.6 left most visits ending on "best pot under threshold" well short of the 2-3 target. |
| `makeabilityErrorFraction` | `0.5` | Doesn't gate candidates on this at all (Beginner never filters by makeability - see below); kept high so it can never become the most forgiving level if that changes. |
| `safetyProbability` | `0.0` | Beginner never *deliberately* plays safe — it always just goes for the ball on, even when that's a bad idea. Any resulting snooker on the human is pure accident. |
| `positionWeight` | `0.0` | No positional planning at all — picks the single easiest legal pot each shot, ignores where the cue ball ends up. |
| `deliberateSpinUsage` | `none` | Always strikes dead-center (`spinOffset = (0,0)`) — no intentional follow/screw/side. Any spin-like outcome is coincidental from mis-hits. |
| `softPotCap` | `2–3` balls per visit | After this many pots in one visit, see pressure ramp below. |
| `pressureRamp` | `+150%` to `aimErrorDegrees` and `powerErrorPercent` per ball potted beyond the cap | Simulates "choking" — makes running much further than 2-3 balls rare without hard-blocking it outright. |
| Candidate survey | Top 2 (shipped; was 1) | Compares the two most obvious candidates rather than blindly taking the first - still far shallower than Medium's 6 or Pro's full survey. |

**Target outcome:** beatable by literally anyone who can aim reasonably straight — the
beginner AI is a soft target that occasionally strings a couple of easy balls together
and nothing more.

## Medium

| Parameter | Value | Why |
|---|---|---|
| `aimErrorDegrees` | random in `[1.5°, 3°]` per shot | Noticeably better than beginner but still misses real cut shots at a real rate. |
| `powerErrorPercent` | `±10%` | Reasonably consistent, not pinpoint. |
| `minAcceptablePotScore` | `0.2` (shipped; `0.4` in the original spec) | Willing to attempt moderately harder shots than beginner, and more of them - lowered once measurement showed 0.4 left most visits far short of target. |
| `makeabilityErrorFraction` | `0.13` | Share of Medium's own aim-error range a pot must survive to count as makeable (see `AI_SHOT_SELECTION.md`) - loosened from an original shared `0.25` across several passes, always kept less forgiving than Pro's. |
| `safetyProbability` | `0.1–0.18` (shipped; `0.15–0.25` originally, roll when no candidate scores above `0.4`) | Plays a genuine safety sometimes, not every time it's the "correct" move — this is deliberate, not perfect, snooker awareness. Dialed back once the per-visit target rose to 7: the original roll frequency meant a real chance of voluntarily ending a run on almost every moderate pot, which was fighting the higher target directly. |
| `positionWeight` | `0.3` | Has some sense of leaving itself a reasonable next shot, not full lookahead. |
| `deliberateSpinUsage` | `basic` — will choose top-spin/stun deliberately for simple position, occasional backspin on straightforward shots; won't attempt precise combination side-spin position play | Matches a club-level player's spin usage. |
| `softPotCap` | `6–8` balls per visit (was `4–5`, then `7–9`) | Re-derived again when the target was corrected down to 6 (3 reds + 3 colours) - see the correction note below. |
| `pressureRamp` | `+80%` to error terms per ball beyond cap | Milder choke than beginner — a decent break is plausible but a full clearance is rare. |
| Candidate survey | Compares top 6 (was top 2-3) by `finalScore`, picks the best | Logged play showed ~25% of Medium's pot decisions had more than 3 *makeable* candidates on the table - `finalScore` was never even computed for those, so a genuinely better-positioned candidate ranked 4th+ by raw pot score could never be picked. Raised to 6 (comfortably above the observed max), still well below Pro's uncapped survey. |

**Target outcome:** beatable, but only by a player who understands basic potting angles
and has some idea of strategy — a casual/new player will lose to this level regularly.

## Pro

| Parameter | Value | Why |
|---|---|---|
| `aimErrorDegrees` | scaled by shot difficulty: `[0.3°, 0.6°]` on easy shots (`potScore > 0.8`) up to `[1.0°, 1.5°]` on hard shots (`potScore < 0.3`) | Real top players still miss hard cuts at a meaningful rate — never treat "pro" as "perfect." |
| `powerErrorPercent` | `±4%` | Very consistent, not flawless. |
| `minAcceptablePotScore` | `0.1` (shipped; `0.2` in the original spec) | Willing to attempt genuinely difficult pots when they're the percentage play, and rarely bails to a soft fallback. |
| `makeabilityErrorFraction` | `0.12` | Loosened further than Medium's, since Pro's own aim error is tiny enough that even a marginal pot mostly still drops. Candidate survey is already full, so there's no deeper-survey lever left for Pro - this is a deliberate, checked loosening. |
| `safetyProbability` | `0.3–0.45` (shipped; was `0.5–0.7`), roll when no candidate scores above `0.35` (was `0.45`) | Plays proper safeties/snookers specifically when there's no good pot, like a real player would, rather than randomly. Dialed back once the per-visit target rose to 12: at the original 0.5-0.7 roll rate, a compounding chance of voluntarily stopping on nearly every moderate pot made a 12-ball run close to impossible regardless of potting accuracy. |
| `positionWeight` | `0.6` | Genuinely plans position for the next ball, will sometimes take a slightly harder pot because it leaves much better position than the "easier" alternative. |
| `deliberateSpinUsage` | `full` — uses screw/follow/side spin deliberately for position control, including combination side-spin+follow/screw | Full toolbox, matching real professional cueing. |
| `softPotCap` | `10–12` balls per visit (was `7–8`, then `12–14`) | Re-derived again when the target was corrected down to 10 (5 reds + 5 colours) - see the correction note below. High ceiling — capable of real breaks. |
| `pressureRamp` | `+40%` to error terms per ball beyond cap, and additionally scale `aimErrorDegrees` up slightly (`+0.1°` per ball already potted this visit, uncapped) even before the soft cap | Even a "pro" gets slightly less precise the longer a break goes on — keeps very long clearances rare rather than routine, without hard-blocking them. |
| Candidate survey | Full survey of all valid candidates, scored by `finalScore` (pot difficulty + position) | Closest to genuine shot selection. |

**Target outcome:** genuinely hard to beat — requires real potting accuracy and some
strategic sense from the human — but structurally guaranteed non-zero miss chance on
every shot (see golden rule) means it is never literally unbeatable. A strong human
player should still be able to win a meaningful fraction of frames.

### Correction (September 2026): the last several passes over-loosened Medium/Pro's gates

Three rounds in a row of "loosen `makeabilityErrorFraction`/`minAcceptablePotScore` further to
chase a higher raw pot-count target" pushed Pro's own pot-attempt failure rate to **24%**
(measured against the same test methodology as every other number in this doc) — visibly
inaccurate potting, and the highest failure rate measured anywhere in this project. Raw
pot-count was never the only goal; it went too far in that direction at the cost of the AI
actually looking competent.

**The targets themselves are now revised down, and accuracy/fouls take priority over hitting
them:**

- **Pro: at least 10 balls per visit (5 reds + 5 colours), not 12.** If reds run out first,
  complete clearance of the remaining colours. Must avoid fouls and visibly play intelligent,
  high-percentage shots — not just attempt anything makeable.
- **Medium: at least 6 balls per visit (3 reds + 3 colours), not 7.** If colours are on the
  table when reds run out, pot 3-4 of them.

`makeabilityErrorFraction` and `minAcceptablePotScore` were both raised well past their
original shared baseline (Pro: fraction `0.12→0.3`, threshold `0.1→0.25`; Medium: fraction
`0.13→0.25`, threshold `0.2→0.3`), `safetyProbability` raised back up (Pro `0.3-0.45→0.4-0.55`,
Medium `0.1-0.18→0.15-0.25`, the latter back to its original spec value), and `softPotCap`
re-derived to the new lower targets (Pro `10-12`, Medium `6-8`). Same seed, before/after:

| Level | Pot-attempt failure | Pots/visit (mean) | Safety fouls |
|---|---|---|---|
| Pro, before this correction | 24% (15/62) | 2.67 | 0/4 |
| Pro, after | **10% (6/62)** | **3.17** | 0/13 |
| Medium, after | 15% (10/66) | 3.11 | 3/8 (small sample) |

Raising the bar didn't just improve accuracy - mean pots/visit went *up* too (fewer failed
marginal attempts ending a visit early more than compensates for being pickier about what's
attempted). Neither level reliably reaches its new floor on every visit yet; most zero-pot
visits are the random open-table test harness handing the AI a table with no genuinely good
pot on it at all (now that the bar for "genuinely good" is higher), not a miss.

All three levels also use `basePowerFraction` (`SnookerAI` component, per scene) `0.85`
for safety/escape power only - pot power is a separate calculation
(`PotPowerFor`/`ControlledPotPowerCeiling`) and unaffected by it. At the original `0.1`,
safeties went out at a barely-moving ~0.12 power; at `0.85` they go out around 0.7.

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
       Medium:   6 balls in THIS visit (3 reds + 3 colours, following the normal
                 red-then-colour alternation - not 6 reds or 6 colours). If colours are
                 still on the table when reds run out, pot 3-4 of them.
       Pro:      10 balls in THIS visit (5 reds + 5 colours, i.e. five full red-then-
                 colour pairs) - OR, if reds happen to run out partway through a visit,
                 the remaining balance of that visit should aim for full clearance of
                 whatever colours are left (up to 6: Yellow/Green/Brown/Blue/Pink/Black)
                 rather than the visit ending early just because the 10 raw-count number
                 was reached by a different mix. Must avoid fouls and visibly play
                 intelligent, high-percentage shots - accuracy and safe shot selection
                 take priority over squeezing out extra balls (see the correction note
                 below - these numbers were previously 7/12 and were pulled back down).
  → only after reaching (at least) that count is it acceptable for the AI's visit to
     end (via a miss or foul) - a visit that pots 0-1 balls and then ends is a FAIL for
     Beginner/Medium, and a visit that pots 0-3 balls and then ends is a FAIL for Pro,
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
15 — Medium/Pro's ranges were raised significantly, see the updated targets above; their
upper bounds and soft-pot-cap values need re-deriving to match, not left at the old
3-4/5-6-era numbers).

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

### Measured against the raised targets (September 2026)

18-visit check per level, fresh open table per visit, same seed across levels, reds/colours
broken down per shot from the debug log:

| Level | Balls/visit (mean) | Visits hitting its minimum | Reds : colours (totals) |
|---|---|---|---|
| Beginner (min 2, unchanged) | 1.38 | 6/16 | consistent with alternation, not separately logged |
| Medium (min 7) | 3.50 | 4/18 | 36 : 27 (≈4:3, matching the target mix) |
| Pro (min 12) | 4.67 | 4/18 | 47 : 39 (≈6:6 on the visits that actually reached 12+) |

Every visit's ball-type sequence was confirmed to strictly alternate Red → Colour → Red
(e.g. Pro's 4 visits that reached 12+ ran `RGRGRBRGRBRBRB` (14), `RPRYRPRBRPRPR` (13),
`RYRBRGRPRPRG` (12), `RYRBRPRBRPRB` (12)) — never two reds or two colours in a row, and
the colour-sequence-after-reds-run-out behaviour didn't need a code change: it already
falls out of `CollectLegalTargets`'s existing fixed-sequence branch once reds hit zero.

**Ordering held** (Pro > Medium > Beginner on both pot rate and pot-attempt quality), but
**neither Medium nor Pro reliably reaches its new floor yet.** Checked in order before
touching any tolerance value:
1. **Candidate survey** — Pro already surveys every makeable candidate; Medium's cap was
   raised 3→6 after logs showed it was genuinely truncating ~25% of decisions (see the
   Medium table above). Left `candidateSurveyCount` alone this round; no further headroom
   found.
2. **Position play** — `finalScore` blending (`potScore*PotWeight + positionScore*
   positionWeight`) is applied correctly and `positionScore` is on the same 0-1 scale as
   `potScore`; no bug found.
3. **Safety-vs-pot decisions** — `softPotCap`/`pressureRamp` were badly out of date for
   the new targets (see the Medium/Pro tables above) and got re-derived; `safetyProbability`/
   `safetyRollPotScore` were dialed back since the old roll frequency meant a real chance
   of voluntarily ending a run on nearly every moderate-difficulty pot, which fights a
   7-or-12-ball target directly.
4. Only after 1-3 showed no further headroom (confirmed by testing them alone first -
   Medium 3.39, Pro 3.89, both flat vs. the previous round) was `makeabilityErrorFraction`/
   `minAcceptablePotScore` loosened one more step.

**Why the new floors aren't reliably hit yet, mathematically, not just "needs more
tuning":** across both rounds of this pass, the dominant way a visit ends is still a
missed pot attempt, at a fairly stable ~15-19% failure rate per attempt regardless of
which of the above got adjusted. Stringing together *n* shots in a row at success rate
*p* happens roughly `p^n` of the time: at 84% per-shot success, 7-in-a-row is ~30% and
12-in-a-row is ~12% - in the right ballpark for what was actually measured (Medium 4/18,
Pro 4/18 after the full round of changes; 0/18 before the tolerance step). Reliably
hitting these floors "nearly every visit" would need per-shot success in the high 90s%,
which isn't reachable through the survey/position/safety/tolerance parameters tried here -
it would need the underlying per-shot miss rate itself driven down (a further look at the
physics/throw model - most remaining failures are still "no contact - within 3deg of
prediction (jaws/pace)", i.e. the object ball left close to the planned line but the pot
was still geometrically marginal) or accepting that "nearly every visit" tolerates more
misses than the current measurement shows.

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
- [ ] Medium pots at least 6 balls (3 reds + 3 colours, or 3-4 colours if colours are on
      the table when reds run out) in EACH individual AI visit (tested across several
      separate visits, not just once), and rarely exceeds 8.
- [ ] Pro plays a deliberate safety when no good pot exists, not just "always attempt."
- [ ] Pro can occasionally miss even a straightforward-looking pot (error floor working).
- [ ] Pro's shot selection is visibly intelligent (position play, safeties when
      appropriate) and it avoids fouls - accuracy is not sacrificed for raw pot count.
- [ ] Pro pots at least 10 balls (5 reds + 5 colours, or full clearance of the colours
      left if reds ran out first) in EACH individual AI visit (tested across several
      separate visits, not just once), rarely exceeds 12, and long visits get visibly
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


