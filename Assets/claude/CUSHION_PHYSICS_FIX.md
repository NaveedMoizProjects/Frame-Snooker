# Cushion Physics Fix

## Symptom
Ball cushion se takra kar wahin ruk jati thi ("bounce" nahi hoti thi) — instead of
reflecting off at a mirror angle like a real snooker cushion.

## Root cause (two separate bugs stacking on each other)

**1. The script read velocity *after* Unity had already killed it.**
`OnCollisionEnter` fires *after* Unity's physics engine has already resolved the
collision using whatever `PhysicMaterial` is on the cushion collider. Unity's default
`PhysicMaterial` has **0 bounciness**. So by the time the old script ran
`rb.velocity * speedMultiplier`, `rb.velocity` was already close to zero — multiplying
~0 by 1.2 is still ~0. The ball looked "stuck" because it basically was: the engine
had already absorbed the impact before the script got a chance to do anything.

**2. `ballLayer` defaults to an empty LayerMask.**
```csharp
[SerializeField] private LayerMask ballLayer = 0; // = "Nothing"
```
If this was never explicitly set to your Ball layer in the Inspector, the very first
line of the old script:
```csharp
if ((ballLayer.value & (1 << collision.gameObject.layer)) == 0) return;
```
returns immediately, every time. The script does **nothing at all**, and cushions fall
back to 100% stock Unity collision response — which is bug #1 all over again.

Between the two, it's very likely the script was silently a no-op on your cushions.

## The fix
`CushionPhysicsMaterial.cs` (replacement attached) no longer depends on the engine's
own collision resolution or the cushion's `PhysicMaterial` bounciness at all. It:

1. Reads the collision contact normal(s) directly.
2. Reflects the ball's horizontal velocity around that normal itself
   (`v' = v - 2*(v·n)*n` — the standard mirror-bounce formula).
3. Applies a `restitution` value (energy kept after the bounce, default 0.92) so it
   doesn't feel perfectly elastic.
4. Slightly damps spin (`spinRetention`) so heavily side-spun balls don't skid forever
   off a rail.
5. Guards against re-reflecting a ball that's already moving away from the cushion
   (important for corner pockets, where two cushions can register contact in the same
   frame).

This makes the bounce correct **regardless of what PhysicMaterial is assigned** to the
cushion — as long as step 2 below is done.

## Required setup in the Unity Editor (the script can't do this part for you)

1. **Set the Ball Layer on every cushion.** Select each cushion GameObject →
   `CushionPhysicsMaterial` component → set **Ball Detection → Ball Layer** to
   whichever layer your balls actually use (check `BallsPrefabs` / the balls in your
   `Balls` hierarchy group for their current layer, e.g. `Ball`). This is almost
   certainly the field that was left unset.
2. **Confirm every ball prefab is actually on that layer.** Cue ball + reds + colours.
3. **Tune `restitution`.** Start at `0.92`, lower it if the table feels too bouncy,
   raise it if bounces feel dead.
4. **Recommended (not required): give cushions + balls a low-friction, low-bounciness
   PhysicMaterial.** Since the script now owns the bounce, you want Unity's own pass to
   interfere as little as possible before `OnCollisionEnter` reads `rb.velocity`. A
   `PhysicMaterial` with `Friction = 0.05`, `Bounciness = 0`, `Friction Combine = Minimum`
   on both the ball and cushion colliders keeps the engine's own contribution minimal.
5. **Rigidbody → Collision Detection = Continuous Dynamic on the ball prefab(s).** A
   small fast-moving ball vs. a thin cushion mesh at high `forceMultiplier` values can
   tunnel straight through on the default `Discrete` mode, which can *also* look like
   "the ball hit the cushion and did nothing" (it actually passed through it, or got
   caught inside it).
6. **Separate, related issue worth checking while you're in here:** `Cue.cs` sets
   `cueballRigidbody.drag = 0.5f` and `angularDrag = 0.5f` in `Start()`. Your
   `BallRollingFriction.cs` script exists specifically to *replace* reliance on
   `Rigidbody.drag/angularDrag` with its own slide→roll→stop model (see its own header
   comment). Leaving `drag = 0.5` on top of that means two separate systems are both
   trying to slow the ball down — this doesn't cause the cushion-sticking bug directly,
   but it will make general rolling feel inconsistent/over-damped. Recommend setting
   `Drag` to `0` (and `AngularDrag` to something small like `0.05`, just to avoid
   infinite numerical spin) on the `Cue` component in the Inspector.

## How to verify it's fixed
1. Play mode → shoot the cue ball straight at a side cushion at low power.
   It should bounce off at a mirrored angle, not stop dead.
2. Shoot it into a corner area where two cushions meet — check it doesn't get double-
   reflected into a weird direction or stick between the two colliders.
3. Shoot at higher power and confirm it doesn't tunnel through (if it does, revisit
   step 5 above - Continuous Dynamic).
