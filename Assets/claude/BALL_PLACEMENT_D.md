# Cue Ball Placement in the D ("Ball in Hand")

## 1. When placement mode triggers

Real snooker has two distinct moments where a player gets to freely position the cue ball
anywhere inside the D (the semicircle at the baulk end of the table):

1. **The very first shot of the frame** — before anyone has struck a ball yet.
2. **After any foul** — the incoming player (the one who did **not** commit the foul)
   gets to place the cue ball anywhere in the D before playing their next shot.

> Note on rule accuracy: strict tournament rules only grant free-D-placement after a foul
> if the cue ball was potted, forced off the table, or otherwise needs repositioning — if
> the cue ball simply stayed on the table after a foul, the opponent normally plays it
> from wherever it lies. You've explicitly asked for placement-in-D after **any** foul
> regardless of where the cue ball ended up, which is a simplified/house-rule version.
> That's a legitimate design choice for a casual game — just flagging that it's a
> deliberate simplification versus strict tournament rules, in case that matters later.

## 2. State machine addition

Add a new `GameManager` state, following the same pattern as `confirmMode`/`inputLocked`:

```
awaitingPlacement : bool
```

- Set `true` when: (a) the game starts and no shot has been taken yet, or (b) `EvaluateFoul()`
  determines a foul occurred (set it for `OpponentIndex`, the non-offending player).
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
- **After a foul:** inside `EvaluateFoul()`'s foul branch (where it currently calls
  `AwardPoints(OpponentIndex, points); PassTurn();`), also set
  `awaitingPlacement = true` for the player who now has `OpponentIndex` as
  `currentPlayerIndex` (i.e., after `PassTurn()` runs, it's exactly the non-offending
  player's turn, so this naturally targets the right player without extra bookkeeping).
- **Existing `cueBallRespawnPoint` field:** currently used as a single fixed spot when the
  cue ball itself is potted mid-shot (`OnBallPotted`). That specific "potted mid-shot"
  respawn can stay as a fixed fallback point *inside* the D (so the ball has somewhere
  sane to sit visually) — but the player should still immediately enter
  `awaitingPlacement` mode afterward per §1, since potting the cue ball is itself a foul.
- **UI:** reuse the same pattern as `ColourNominationUI`/`GameManager`'s manual panel
  toggling — a placement-prompt panel shown/hidden based on `awaitingPlacement`, ideally
  with a short status label ("Place the cue ball in the D") consistent with how
  `ScoreboardUI` and the nomination panel already read GameManager state reactively.

## 6. Order of operations per shot (updated)

```
Frame start
  → awaitingPlacement = true (player 1 places ball in D)
  → normal aim / spin / power / strike flow (as documented in SPIN_LOGIC.md and
     GAME_MECHANICS.md)
  → OnAllBallsStopped → EvaluateFoul()
       legal  → normal continue, no placement
       foul   → awaitingPlacement = true for the new current player (after PassTurn)
  → if awaitingPlacement: block Confirm/Strike, show D + drag input, until a valid
     placement is made
  → repeat
```
