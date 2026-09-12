# AI Foul Logic — Consolidated Reference

This pulls together everything the AI needs to get right about fouls into one place, since
playtesting found the AI effectively "not understanding" which ball it's allowed to hit.
Read this alongside `AI_SHOT_SELECTION.md` (the algorithm), `AI_DIFFICULTY_LEVELS.md` (the
per-level tuning), and `SCORING_FOUL_HITTING_AUDIT.md` (the original rules audit) — this
doc doesn't replace those, it's the foul-specific checklist to build and test against.

## 1. The one rule that must be perfect at every level: which ball is "on"

At the start of every single shot, exactly one of these is true, and the AI's candidate
generation must be restricted to ONLY that:

```
GameManager.CurrentTargetState == Red        → legal targets = every Red still on table
GameManager.CurrentTargetState == Colour
   AND CurrentTargetColour has a value        → legal targets = only that one colour
   AND CurrentTargetColour is null (needs
       nomination, reds still remain)         → legal targets = any of the 6 colours;
                                                  AI picks its shot, then nominates
                                                  whichever colour that shot was on
   AND no reds remain (fixed sequence)         → legal targets = only the next colour
                                                  in Yellow→Green→Brown→Blue→Pink→Black
```

This is **not a difficulty setting** — it must be 100% correct at Beginner, Medium, and
Pro alike. A weak AI is allowed to miss the correct ball; it is never allowed to aim at
the wrong one on purpose. `SnookerAI.cs`'s `CollectLegalTargets()` already implements this
correctly in isolation — the bug found in this project was upstream of it, in
`GameManager` itself (see §2).

## 2. Confirmed bug: `GameManager.EvaluateShotResult()` can permanently lock the game onto "Colour"

Found during playtesting and confirmed by reading `GameManager.cs` directly (it's
self-documented in the code as a "known gap"): if a red is potted **as part of a foul
shot** (e.g. the cue ball hit a colour first — a foul — but a red also happened to fall
in on the same shot), `targetState` still flips from `Red` to `Colour`, exactly as if it
had been a clean legal red pot. Real snooker rule: after a foul, the incoming player is
still "on Red" if reds remain — this flip should **not** happen on a foul shot.

Because nothing else can flip `targetState` back to `Red` except a *legal* colour pot
while reds remain, this single wrong flip **permanently locks the entire rest of the
frame into Colour mode** — every subsequent shot for both players will only ever
legally target colours, even though reds are still sitting on the table untouched. This
fully explains "the AI only ever plays colours, never red": the AI's own logic is
correct, it's faithfully following a `CurrentTargetState` that GameManager put into a
bad state after the very first foul-that-also-pots-a-red.

**Fix:** `EvaluateFoul()` already runs before `EvaluateShotResult()` (existing
subscription order), so foul status is known in time. `EvaluateShotResult()` must skip
the `targetState = Colour` transition specifically when the current shot was a foul —
state stays on `Red` in that case. Legal (non-foul) red pots still transition normally.

## 3. Every foul type the AI must actively avoid (not just score correctly)

`SCORING_FOUL_HITTING_AUDIT.md` already confirmed `GameManager`'s foul *scoring* math is
correct (§B1–B11). The gap is that the AI's *shot selection* needs to actively steer away
from causing these, not just rely on the scoring being right after the fact:

| Foul | How the AI avoids it |
|---|---|
| Hitting the wrong ball first | Solved by §1/§2 above — correct legal-target filtering, with the GameManager bug fixed so the target never silently becomes wrong mid-frame. |
| Potting the cue ball | `TryFindContact`/`GenerateCandidates` aim at ghost-ball contact points on the legal ball, never at a pocket directly with the cue ball as the "object" — this shouldn't happen from correct candidate geometry. Verify no code path ever aims the cue ball itself at a pocket line. |
| Hitting nothing at all | `PlanShot()` always aims at a real legal-target contact point (via `TryFindContact`/`NearestTo`) even in the escape/safety fallback paths — there's always a real ball being aimed at, never a swing at empty space. Verify `BuildEscape`'s fallback (`aim = Flat3(pockets[0] - cueBallPos)`, used only if literally no legal ball exists on the table) can't trigger while balls remain. |
| Potting a colour while on Red | Prevented structurally: while `CurrentTargetState == Red`, `CollectLegalTargets` only ever returns reds, so `GenerateCandidates` never even builds a colour-ball candidate to pot in the first place. |
| Potting the wrong colour while on Colour | Same structural prevention — `CollectLegalTargets` returns only the one legal colour. |
| Miscue / mishit from extreme spin | N/A here — see `SPIN_LOGIC.md` §6, the usable drag radius is already clamped so this can't occur by construction; not applicable to the AI's spin choices either since `ChooseSpin` only selects from the fixed `BasicSpins`/`FullSpins` arrays, all well inside safe range. |

## 4. Foul-rate targets per level (restated from `AI_DIFFICULTY_LEVELS.md` for convenience)

- **Decision-logic fouls** (wrong-ball-type targeting): **zero, at every level**, always —
  this is the §1/§2 fix, not a tuning knob.
- **Physical/aim-driven fouls** (aimed at the right ball, error caused a mis-hit): allowed
  to scale — Beginner noticeably present, Medium rare, Pro extremely rare (never
  mathematically zero, per the golden rule, but should be very uncommon in practice).

## 5. Testing checklist specific to foul logic

- [ ] Deliberately let the AI foul while a red is on the table (e.g. by testing at
      Beginner, where high aim error makes this likely) and confirm `CurrentTargetState`
      stays `Red` afterward, not `Colour`.
- [ ] Confirm the AI never once, across many visits at any level, attempts a shot on a
      ball outside the current legal target set (log every `PlanShot()` decision's
      target type against `CurrentTargetState`/`CurrentTargetColour` at that moment and
      diff them).
- [ ] Confirm potting rates recover to the per-visit minimums in `AI_DIFFICULTY_LEVELS.md`
      once this fix lands — a large part of "bad potting" was very likely the AI being
      structurally locked into attempting Colour shots it had little chance of finding a
      makeable candidate for, immediately after the state got stuck.
