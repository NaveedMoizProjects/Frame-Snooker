# Spin / Side Logic (Follow, Stun, Screw, English)

## 1. What exists today vs. what's being replaced

`Cue.cs` currently has `angleOffsetDegrees` and `verticalAngleDegrees` sliders, applied in
`CalculateForceDirection()` by literally **rotating the strike direction itself**, plus a
separate `AddTorque()` with a fixed `0.1×` magnitude. This is not how real spin works —
rotating the direction changes *where the ball goes on the first shot*, not how it curves
or behaves after contact. Real English doesn't meaningfully bend the initial straight-line
path; it changes what the ball does **after** it touches something (an object ball, a
cushion, or the cloth over time). This whole approach needs replacing with the model below.

## 2. The 2D hit-point UI (the "dot")

- A circular overlay of `cueBallSprite` is shown during aiming (e.g. corner of screen or
  next to the power slider).
- A draggable dot sits on top of it, starting at dead center by default each shot.
- Dot position is stored as a normalized 2D vector `spinOffset = (dx, dy)`, each in
  `[-1, 1]`, clamped so `dx² + dy² ≤ 1` (stays inside the circle).
- **Axis meaning** (matches how a player looks at the cue ball from behind, along the
  strike direction):
  - `dy > 0` (dot near top) → **topspin / follow**
  - `dy < 0` (dot near bottom) → **backspin / screw**
  - `dx > 0` (dot right) → **right-hand side spin**
  - `dx < 0` (dot left) → **left-hand side spin**
  - `(0, 0)` (dead center) → **no spin at all** — this is intentionally NOT special-cased
    as "force stun," see §4.
- Recommend clamping the usable radius to ~85% of the full circle (`dx²+dy² ≤ 0.85²`) and
  leaving the outer ring purely visual/unusable — hitting at the extreme edge of a real
  cue ball risks a miscue (mis-hit). A miscue mechanic is optional polish, not required for
  this pass — just leave the outer edge unreachable by the drag so it can't happen.

## 3. Physically correct strike model (replaces the old rotate-direction approach)

The key idea: **apply the strike impulse at an offset point on the cue ball's surface,
not through its center**, and let Unity's own physics turn that into the correct
combination of linear velocity *and* spin automatically — exactly like
`BallRollingFriction.cs` already does for table friction via `AddForceAtPosition`.

```
Given at strike time:
  aimForward = normalized direction from cue tip to ball center      (unchanged - pure aim)
  right      = normalize(Cross(Vector3.up, aimForward))
  up         = Vector3.up
  radius     = cue ball radius (already read elsewhere in Cue.cs)
  maxOffset  = radius * 0.7f   // how far off-center the tip can strike; tune by feel

  sideOffset    = right * (spinOffset.x * maxOffset)
  verticalOffset = up   * (spinOffset.y * maxOffset)

  // Point on the ball's surface, on the side facing the cue stick, offset by the dot.
  contactPoint = ballCenter - aimForward * radius + sideOffset + verticalOffset

  finalImpulse = aimForward * (baseStrikeForce * forceMultiplier)   // aim direction NEVER changes

  cueballRigidbody.AddForceAtPosition(finalImpulse, contactPoint, ForceMode.Impulse)
```

That single `AddForceAtPosition` call replaces BOTH the old `AddForce(...)` and the old
`AddTorque(...)` — Unity derives the correct angular impulse from the offset automatically
(`τ = r × F`), so there's no separate spin-torque calculation needed and `applySpinTorque`
/ the old spin-magnitude constant can be removed.

## 4. Why this naturally produces every behavior you described, with no special-casing

| Dot position | What the code does | Resulting behavior |
|---|---|---|
| Dead center, straight shot into another ball | Zero offset → impulse through the center → pure linear velocity, no spin | Cue ball transfers essentially all its forward momentum into the object ball on a full hit → **stun** (dead stop). This is a natural physics outcome, not a hardcoded rule. |
| Dead center, but it's a **cut/angled shot** | Same zero-spin impulse, but the existing collision geometry (tangent-line deflection, same math your aim-prediction line already uses) still applies | Cue ball naturally continues along the tangent line at an angle — **not** forced to stun, exactly as you described. Nothing special needs to be coded for this case; it already falls out of correct physics + zero spin. |
| Full top (`dy=1, dx=0`) | Topspin applied via the offset above the equator | Once `BallRollingFriction.cs`'s slide→roll transition kicks in, the forward-matching spin accelerates the ball forward after contact → **follow**. |
| Top-right (`dy=1, dx>0`) | Topspin + right-side spin combined | Follow forward, curving right — both effects stack because both offsets are baked into the same single contact point. |
| Top-left (`dy=1, dx<0`) | Topspin + left-side spin | Follow forward, curving left. |
| Full bottom (`dy=-1, dx=0`) | Backspin, offset below equator | Spin opposes the rolling direction, so once slide friction acts, the ball reverses → **screw back**, matching real snooker. |
| Bottom-right / bottom-left | Backspin + side spin combined | Screw back, curving to that side — same stacking as the top cases. |

No `if (dy > 0.9f) { ApplyFollowHack(); }`-style special cases are needed anywhere — the
single offset-impulse call is the entire implementation. This is important: **do not**
reintroduce hardcoded behavior branches for "full top" / "full bottom" / "center" — the
whole point of this model is that they emerge correctly on their own.

## 5. Interaction with existing systems

- `BallRollingFriction.cs` already computes contact-point slip using linear + angular
  velocity at the table contact point — this is exactly the mechanism that turns the
  spin applied above into visible follow/screw/curve behavior over time. **No changes
  needed there.**
- `CushionPhysicsMaterial.cs`'s spin damping on bounce (`spinRetention`) still applies
  normally — side-spin surviving a cushion contact (swerve continuing after a rail) is
  already handled by that existing field, nothing new needed.
- Aim-prediction line (`GenerateAimPrediction`) should keep using the pure `aimForward`
  direction (unaffected by spin) for the straight-segment part, since the initial launch
  direction genuinely doesn't change — only note in the UI (e.g. a small curved hint arc)
  if you want to visually communicate the expected curve; that's optional polish, not
  required for correct physics.
- Reset `spinOffset` to `(0,0)` at the start of every new shot (`RequestStrike()` is the
  natural place), so spin doesn't carry over and surprise the player next turn.

## 6. Reference UI layout (match this exactly)

- Bottom-left corner of the aiming screen: a large translucent circular widget with a
  small red-ringed dot marking the currently selected spin point, and the label
  "DRAG TO ADJUST SPIN" beneath it.
- A small collapsed/preview version of the same dot sits pinned in the far top-left
  corner of the screen at all times (a mini indicator of current spin so the player
  doesn't have to keep the full widget open to remember their setting).
- The widget is draggable directly (tap/drag anywhere inside the circle to move the dot,
  not just on the dot itself), clamped to the circle per §2.
- This widget should only be interactable during free-aim (same visibility/interactable
  gating as `!GameManager.IsConfirmMode`, matching how `ShotPowerSlider` only becomes
  interactable once confirmed - here it's the inverse, it should lock/hide once confirmed
  since spin is locked in before the power slider stage).

## 7. Miscue rule (optional, not required for this pass)

Real snooker: hitting too close to the edge of the cue ball's surface causes a "miscue" —
a weak, inaccurate, sometimes-foul shot. Since §2 already clamps the usable drag radius to
~85%, miscues are structurally prevented rather than simulated. Leave it this way unless
you specifically want miscue-as-a-foul added later — flagging as a deliberate scope
decision, not an oversight.
