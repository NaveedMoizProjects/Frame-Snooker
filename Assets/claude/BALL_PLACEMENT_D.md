# Cue Ball Placement in the D ("Ball in Hand")

## 1. When placement mode triggers — CORRECTED RULE

**Only one trigger condition, confirmed by the user after playtesting the "any foul"
version and finding it wrong:**

1. **The very first shot of the frame** — before anyone has struck a ball yet.
2. **The cue ball is potted** (or otherwise needs repositioning) — regardless of whether
   that potting happened as part of a foul or any other circumstance, placement triggers
   specifically because the cue ball itself is off the table and needs to go back on,
   not because a foul occurred in general.

**Placement must NOT trigger on other fouls where the cue ball stayed on the table** —
e.g. hitting the wrong ball first, or potting the wrong colour, while the cue ball itself
remains on the table: those are still fouls (existing `EvaluateFoul` scoring is
unaffected), turn still passes, but the incoming player simply plays the cue ball from
wherever it's resting — no placement UI, no D. This matches real tournament snooker rules
and corrects an earlier draft of this doc that incorrectly specified "after any foul."

If `CueBallContactTracker`/`PocketTrigger`/`GameManager.OnBallPotted` already has a clean
signal for "the cue ball was potted this shot" (it does — `cueBallPotted` is already
computed inside `EvaluateFoul` from `PottedThisShot`), reuse that exact signal to decide
whether to enter `awaitingPlacement` — do not derive it from "a foul happened" more
broadly.

## 2. State machine addition

Add a new `GameManager` state, following the same pattern as `confirmMode`/`inputLocked`:

```
awaitingPlacement : bool
```

- Set `true` when: (a) the game starts and no shot has been taken yet, or (b) the cue
  ball was potted this shot (check the same `cueBallPotted` flag `EvaluateFoul` already
  computes — do this regardless of whether the shot was otherwise a foul or, in the rare
  case cue-ball-potted-but-somehow-legal doesn't apply here since potting the cue ball is
  always itself a foul, so this only ever fires alongside a foul, but is driven by the
  cue-ball-potted condition specifically, not "any foul").
- **Do not** set `awaitingPlacement = true` for fouls where `cueBallPotted` is false.
- While `true`:
  - `ConfirmButtonPressed()` and `RequestStrike()` must both refuse to proceed (mirror the
    existing `NeedsColourNomination` guard pattern already in `ConfirmButtonPressed()`).
  - `CueVisualController` / normal aiming input is disabled (reuse `IsInputLocked`-style
    gating), replaced by placement-drag input instead.
- Set back to `false` the moment the player confirms a valid placement point.

## 3. The D — geometry

The D's exact position/size depends on your table model, which isn't something this doc
can hardcode. Before writing placement-validation code, determine the real geometry from
the actual scene:

- Find the baulk line's Z coordinate (it's a marked line on your table mesh/texture, and
  is also where the Green/Brown/Yellow spots sit at the D's ends and center — those three
  ball spawn spots, from `BallIdentity` on the Green/Brown/Yellow balls, already tell you
  exactly where the baulk line is and how wide the D is, since Brown sits at the D's
  center point and Green/Yellow sit at its two corners).
- The D's radius is the standard distance from the baulk line's center to the Green/Yellow
  spot's baulk-line-perpendicular offset — again, derivable directly from those two
  existing ball spawn positions already in the scene rather than guessed.
- Store this as a simple runtime-computed struct: baulk line Z, D center X, D radius —
  computed once at `Start()` from the Green/Brown/Yellow `BallIdentity.SpawnPosition`
  values already present in the scene, so it automatically matches whatever table you're
  using without hardcoded magic numbers.

## 4. Placement input & validation

- While `awaitingPlacement` is true, show a visual indicator of the D boundary (simple
  line/arc renderer, or a semi-transparent overlay) so the player can see where they're
  allowed to drop the ball.
- Player drags the cue ball (or a placement marker/ghost) with mouse/touch.
- On release, validate the point:
  1. Must be inside the D semicircle (distance from D center ≤ D radius, **and** on the
     baulk side of the baulk line — it's a half-circle, not a full circle).
  2. Must not overlap any other ball currently on the table (check distance ≥ 2× ball
     radius against every active ball in `GameManager.GetBalls()`).
  3. If invalid, don't accept the drop — snap back to the last valid position (or the
     default baulk-line-center spot) and let the player try again, same as how invalid
     drops are commonly handled in pool/snooker games.
- On a valid drop: set `cueBall.transform.position`, zero its velocity/angular velocity,
  set `awaitingPlacement = false`, and hand control back to normal aiming.

## 5. Integration points with existing code

- **Frame start:** in `GameManager.Start()`, set `awaitingPlacement = true` instead of
  leaving the cue ball at whatever position it was left at in the scene/editor.
- **Cue ball potted (the ONLY other trigger — not every foul):** inside `EvaluateFoul()`,
  wherever `cueBallPotted` is already computed, after `PassTurn()` runs, additionally set
  `awaitingPlacement = true` for the player who now has `currentPlayerIndex` (the
  non-offending player). If `cueBallPotted` is false for a given foul, do **not** set
  `awaitingPlacement` — the incoming player just plays from wherever the cue ball already
  is on the table.
- **Existing `cueBallRespawnPoint` field:** currently used as a single fixed spot when the
  cue ball itself is potted mid-shot (`OnBallPotted`). That specific "potted mid-shot"
  respawn can stay as a fixed fallback point *inside* the D (so the ball has somewhere
  sane to sit visually the instant it's potted) — `awaitingPlacement` then immediately
  takes over per §1/§2 so the player can move it anywhere else in the D if they want.
- **UI:** reuse the same pattern as `ColourNominationUI`/`GameManager`'s manual panel
  toggling — a placement-prompt panel shown/hidden based on `awaitingPlacement`, ideally
  with a short status label ("Place the cue ball in the D") consistent with how
  `ScoreboardUI` and the nomination panel already read GameManager state reactively.

## 6. Reference UI layout (match this exactly)

- Table view stays visible (not a separate screen) with a centered "PLACE THE CUE BALL"
  label near the bottom of the table area.
- A horizontal pill-shaped control below that label lets the player drag/swipe to rotate
  the camera around the table while placing ("DRAG HERE TO ROTATE CAMERA"), with
  left/right chevron arrows on either end of the pill as a visual hint. This does not
  move the ball — it's purely a camera-orbit control so the player can see the D from a
  better angle before dropping the ball.
- The D outline itself is drawn directly on the table (thin white arc, matching the D
  marking already on your table texture/mesh) with a small ring/marker at its center
  showing exactly where the cue ball will land if dropped there.
- A green circular checkmark button, bottom-right of the screen, confirms the current
  placement (equivalent to the `awaitingPlacement = false` transition in §2). Keep this
  disabled/unpressable until the current position passes the validation in §4.
- Score bar (player names/avatars/score) and the pause button stay visible and unchanged
  in their normal positions during placement — don't hide the rest of the HUD, only swap
  the bottom-center controls from the normal aim/power UI to the placement UI described
  here.
- The spin-selector widget from `SPIN_LOGIC.md` should be hidden during placement (spin
  is irrelevant until aiming actually starts).

## 7. Order of operations per shot (corrected)

```
Frame start
  → awaitingPlacement = true (player 1 places ball in D)
  → normal aim / spin / power / strike flow (as documented in SPIN_LOGIC.md and
     GAME_MECHANICS.md)
  → OnAllBallsStopped → EvaluateFoul()
       legal, cue ball not potted        → normal continue, no placement
       foul, cue ball NOT potted         → normal continue, no placement (opponent
                                            plays cue ball from where it lies)
       foul, cue ball WAS potted         → awaitingPlacement = true for the new
                                            current player (after PassTurn)
  → if awaitingPlacement: block Confirm/Strike, show D + drag input, until a valid
     placement is made
  → repeat
```
