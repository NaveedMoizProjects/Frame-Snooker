using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// The single shot-selection algorithm from AI_SHOT_SELECTION.md, shared by all three difficulty
// levels. Nothing here branches on "which level am I" - the level is entirely the AIDifficultyProfile
// asset wired into this component, so Beginner/Medium/Pro are three sets of numbers, not three
// scripts. Everything it does goes through the same GameManager/Cue calls a human's input produces.
public class SnookerAI : MonoBehaviour
{
    [Header("Scene refs (left empty = found at Start)")]
    [SerializeField] private GameManager gameManager;
    [SerializeField] private Cue cue;
    [SerializeField] private CueVisualController cueVisual;
    [SerializeField] private ShotPowerSlider powerSlider;

    [Header("Difficulty")]
    [Tooltip("Beginner / Medium / Pro asset. This is the ONLY thing that differs between levels.")]
    [SerializeField] private AIDifficultyProfile profile;

    [Tooltip("Which of GameManager's two players this AI plays as. The other index is the human.")]
    [SerializeField] private int aiPlayerIndex = 1;

    [Header("Feel")]
    [Tooltip("Pause before shooting, purely so the AI doesn't feel instant. Not a functional delay.")]
    [SerializeField] private Vector2 thinkingDelaySeconds = new Vector2(0.5f, 1.5f);

    [Header("Power mapping")]
    [Tooltip("Power fraction for a safety or escape with no distance to cover at all. Pots are paced " +
             "separately, from what the object ball needs to reach the pocket (PotPowerFor).")]
    [SerializeField] private float basePowerFraction = 0.10f;
    [Tooltip("Extra power fraction per full table diagonal a safety or escape has to travel.")]
    [SerializeField] private float powerPerTableDiagonal = 0.35f;
    [Tooltip("Safeties are played at this fraction of the power the same distance would normally get. " +
             "Below about 0.5 the cue ball stops short of the ball on, which is a foul, not a safety.")]
    [SerializeField] private float safetyPowerMultiplier = 0.8f;

    [Tooltip("Power fraction for the break shot - the firm hit into a still-racked pack when there is " +
             "nothing on. A soft safety here just taps the pack and hands back the same position.")]
    // 0.35 puts the cue ball around 7.6 m/s, roughly 18 units of travel - enough to reach the pack
    // from the D and spread it. 0.65 measured 13.7 m/s, about 33 units on a 16.6-unit table, so the
    // ball crossed twice and ricocheted; that read as "the balls are moving much faster now" once the
    // targetState fix stopped the AI being stuck on Colour and let it actually play break shots.
    [SerializeField] private float breakPowerFraction = 0.35f;

    [Header("Shot selection margins")]
    [Tooltip("Extra room, as a fraction of the ball radius, the AI insists on down a shot line before " +
             "it will commit to it. Zero means it will try to thread gaps it cannot physically hit - " +
             "off the break that means grazing the pink and fouling.")]
    // 0.25 is deliberate: the break-off line that used to graze the pink had 0.045 units of room,
    // which is 0.23 ball radii, so this still rejects it - while 0.5 was throwing away half the
    // legitimate candidates on a cluttered table (measured 6 -> 3 on one mid-frame position).
    // Re-checked for general play near a red cluster (logged gaps per shot, ~400 pots): misses did not
    // concentrate on tight-gap lines and none began with the object ball clipping a neighbour - the big
    // off-line launches were pace (see MaxControlledImpactSpeed) - so 0.25 still holds.
    [SerializeField] private float sightMarginBallRadii = 0.25f;
    [SerializeField] private Vector2 powerFractionLimits = new Vector2(0.06f, 0.85f);

    [Header("Debug")]
    [Tooltip("Logs the full decision for every shot: chosen target, scores, and the exact aim/power " +
             "error injected. This is what the difficulty sanity checklist is verified against.")]
    [SerializeField] private bool debugLogging = true;

    private const float MaxCutAngleDegrees = 85f;

    // Impact parameters sampled when building a safety: 0 is a full-ball hit, +/-0.85 is as thin as
    // the AI will try to clip the ball on.
    private static readonly float[] SafetyContacts = { -0.85f, -0.6f, -0.3f, 0f, 0.3f, 0.6f, 0.85f };

    // Same set ordered thickest-first, for when the AI only needs *a* legal contact and would rather
    // have a controllable one. Off the break nothing can be hit full - the pink screens the apex and
    // the pack screens itself - so only a thin clip on an outside red is legal, exactly as in the
    // real game. Testing reachability against ball centres alone would call that "snookered".
    private static readonly float[] ContactSamples = { 0f, 0.3f, -0.3f, 0.6f, -0.6f, 0.85f, -0.85f };

    private static readonly Vector2[] BasicSpins =
    {
        Vector2.zero, new Vector2(0f, 0.5f), new Vector2(0f, -0.5f)
    };

    private static readonly Vector2[] FullSpins =
    {
        Vector2.zero, new Vector2(0f, 0.6f), new Vector2(0f, -0.6f),
        new Vector2(0.45f, 0f), new Vector2(-0.45f, 0f),
        new Vector2(0.4f, 0.5f), new Vector2(-0.4f, 0.5f),
        new Vector2(0.4f, -0.5f), new Vector2(-0.4f, -0.5f)
    };

    private Rigidbody cueBall;
    private readonly List<Vector3> pockets = new List<Vector3>(6);
    private float tableDiagonal;
    // Per pocket: how close (flat, centre to centre) a ball has to get to the pocket trigger's centre
    // before its collider touches the trigger and it drops. Read off the trigger, not guessed.
    private readonly List<float> pocketReach = new List<float>(6);
    // Everything a ball physically collides with - cushions, jaws, stray scenery - so the pocket entry
    // test sees exactly the geometry the ball will meet.
    private int ballBlockerMask;
    private readonly RaycastHit[] entryHits = new RaycastHit[16];

    private readonly List<ShotCandidate> candidates = new List<ShotCandidate>(128);
    private readonly List<Rigidbody> targetBuffer = new List<Rigidbody>(16);
    private readonly List<Rigidbody> nextTargetBuffer = new List<Rigidbody>(16);
    private int candidatesGenerated;

    private bool waitingForShot;
    private float waitingForShotSince;
    private bool shotWasMine;
    private bool visitActive;
    private int ballsPottedThisVisit;
    private int softPotCapThisVisit;

    private struct ShotCandidate
    {
        public Rigidbody objectBall;
        public BallType type;
        public Vector3 ghost;
        public Vector3 pocket;
        public int pocketIndex;
        public Vector3 cueBallPos;
        public Vector3 aimDir;
        public float cutAngle;
        public float span;
        public float cueToGhost;
        public float objectToPocket;
        public float potScore;
        public float positionScore;
        public float finalScore;
        public Vector2 spin;
        public float powerFraction;
        public bool makeable;
    }

    private struct ShotPlan
    {
        public Vector3 aimDir;
        public Vector2 spin;
        public float powerFraction;
        public BallType? nominate;
        public bool isSafety;
        public float potScore;
        public float finalScore;
        public float aimErrorDegrees;
        public float powerErrorPercent;
        public string target;
        public int candidatesGenerated;
        public int candidateCount;

        // Pot attempts only - what the debug tracker compares the real result against.
        public Rigidbody objectBall;
        public Vector3 pocket;
        public Vector3 cueBallPos;
        public float cueToGhost;
        public float objectToPocket;
        public float expectedThrowDegrees;
        public Vector3 predictedCueRest;
        public string clearance;
    }

    void Start()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
        if (cue == null) cue = FindObjectOfType<Cue>();
        if (cueVisual == null) cueVisual = FindObjectOfType<CueVisualController>();
        if (powerSlider == null) powerSlider = FindObjectOfType<ShotPowerSlider>();

        if (gameManager == null || cue == null || cueVisual == null || powerSlider == null || profile == null)
        {
            Debug.LogError("SnookerAI: needs a GameManager, Cue, CueVisualController, ShotPowerSlider " +
                           "and an AIDifficultyProfile. Disabling.", this);
            enabled = false;
            return;
        }

        foreach (var ball in gameManager.GetBalls())
        {
            var id = ball != null ? ball.GetComponent<BallIdentity>() : null;
            if (id != null && id.Type == BallType.Cue) cueBall = ball;
        }
        if (cueBall == null)
        {
            Debug.LogError("SnookerAI: no ball in GameManager's list has BallIdentity type Cue.", this);
            enabled = false;
            return;
        }

        int ballLayer = cueBall.gameObject.layer;
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(ballLayer, layer)) ballBlockerMask |= 1 << layer;

        foreach (var pocket in FindObjectsOfType<PocketTrigger>())
        {
            var trigger = pocket.GetComponent<Collider>();
            Vector3 centre = trigger != null ? trigger.bounds.center : pocket.transform.position;
            float triggerRadius = trigger != null ? Mathf.Min(trigger.bounds.extents.x, trigger.bounds.extents.z) : 0f;

            // The trigger is a sphere sitting a little below the balls' plane, so the flat distance at
            // which a ball first touches it is less than the two radii added together.
            float touch = triggerRadius + cue.CueBallRadius;
            float drop = centre.y - cueBall.position.y;
            pockets.Add(pocket.transform.position);
            pocketReach.Add(Mathf.Sqrt(Mathf.Max(0f, touch * touch - drop * drop)));
        }

        // The table's own size, straight off the pocket layout, so nothing here is a magic number
        // tied to one table model.
        for (int i = 0; i < pockets.Count; i++)
            for (int j = i + 1; j < pockets.Count; j++)
                tableDiagonal = Mathf.Max(tableDiagonal, Flat3(pockets[j] - pockets[i]).magnitude);

        if (pockets.Count == 0 || tableDiagonal <= 0f)
        {
            Debug.LogError("SnookerAI: found no PocketTriggers to read the table geometry from.", this);
            enabled = false;
            return;
        }

        // Subscribing here rather than in Awake puts this handler after GameManager's own
        // foul/result handlers, so by the time it runs the turn has already been passed or kept.
        gameManager.OnAllBallsStopped += HandleShotResolved;

        // Lets GameManager refuse a human's stray Confirm/nominate/strike click that lands during the
        // AI's own turn (none of that UI is turn-gated on its own) without ever blocking the AI's own
        // calls to those same methods - see SetAiActing around TakeShot's confirm/strike sequence.
        gameManager.RegisterAiPlayer(aiPlayerIndex);

        if (debugLogging)
            Debug.Log($"[AI:{profile.name}] Ready as Player {aiPlayerIndex}. " +
                      $"Table diagonal {tableDiagonal:F2}, {pockets.Count} pockets.");

        StartCoroutine(PlayLoop());
    }

    void OnDestroy()
    {
        if (gameManager != null) gameManager.OnAllBallsStopped -= HandleShotResolved;
    }

    private float BallDiameter => cue.CueBallRadius * 2f;
    private float SightMargin => cue.CueBallRadius * Mathf.Max(0f, sightMarginBallRadii);

    // ---------------------------------------------------------------- 1. When the AI acts
    private bool MyTurnToAct()
    {
        return !gameManager.IsFrameOver
            && gameManager.CurrentPlayerIndex == aiPlayerIndex
            && gameManager.isNextPlay()
            && !gameManager.IsConfirmMode
            && !gameManager.IsStrikeRequested
            && !waitingForShot;
    }

    // Hard ceiling on how long a real shot can take to resolve (aim settle + strike + every ball
    // rolling to rest). Measured shots never come close to this - it exists purely so that if
    // waitingForShot ever gets stuck true for a reason nobody has seen yet, the AI recovers on its own
    // within a few seconds instead of sitting dead for the rest of the frame.
    private const float MaxShotResolutionSeconds = 15f;

    private IEnumerator PlayLoop()
    {
        while (true)
        {
            yield return null;

            if (waitingForShot && Time.time - waitingForShotSince > MaxShotResolutionSeconds)
            {
                Debug.LogWarning($"[AI:{profile.name}] waitingForShot stuck for {MaxShotResolutionSeconds}s with no " +
                                  "OnAllBallsStopped - forcing it clear so the AI can try again.");
                waitingForShot = false;
                shotWasMine = false;
            }

            if (!MyTurnToAct()) continue;

            // FOUL_PLAY_AGAIN_RULE.md section 4: this decision happens BEFORE placement-in-D or
            // normal aiming, and before the visit-bookkeeping below - a "play again" choice hands
            // the turn straight back to the fouling player, so this was never really the start of
            // an AI visit at all.
            if (gameManager.IsAwaitingFoulDecision)
            {
                yield return DecideFoulPlayOrAgain();
                continue;
            }

            if (!visitActive)
            {
                visitActive = true;
                ballsPottedThisVisit = 0;
                softPotCapThisVisit = profile.RollSoftPotCap();
            }

            if (gameManager.IsAwaitingPlacement)
            {
                yield return PlaceCueBall();
                continue;
            }

            yield return new WaitForSeconds(Random.Range(thinkingDelaySeconds.x, thinkingDelaySeconds.y));
            if (!MyTurnToAct() || gameManager.IsAwaitingPlacement) continue;

            yield return TakeShot();
        }
    }

    // ---------------------------------------------------------------- 8. Turn loop
    private void HandleShotResolved()
    {
        waitingForShot = false;
        if (!shotWasMine) return;
        shotWasMine = false;

        int potted = 0;
        foreach (var ball in gameManager.PottedThisShot)
            if (ball != cueBall) potted++;

        if (gameManager.CurrentPlayerIndex == aiPlayerIndex)
        {
            // Still our visit, so the pressure ramp keeps climbing - it never blocks the next shot,
            // it only makes it shakier.
            ballsPottedThisVisit += potted;
            if (debugLogging)
                Debug.Log($"[AI:{profile.name}] OUTCOME={(potted > 0 ? "POTTED " + potted : "MISS")} (turn kept). " +
                          $"Visit now {ballsPottedThisVisit} (soft cap {softPotCapThisVisit}), " +
                          $"error x{PressureMultiplier():F2}.");
        }
        else
        {
            if (debugLogging && visitActive)
                Debug.Log($"[AI:{profile.name}] OUTCOME={(potted > 0 ? "POTTED " + potted + " but" : "MISS/FOUL -")} " +
                          $"turn lost. Visit over after {ballsPottedThisVisit} ball(s).");
            visitActive = false;
        }
    }

    private float PressureMultiplier()
        => 1f + profile.pressureRamp * Mathf.Max(0, ballsPottedThisVisit - softPotCapThisVisit);

    // ---------------------------------------------------------------- 2. Perceiving the table
    private int RedsRemaining()
    {
        int count = 0;
        foreach (var ball in gameManager.GetBalls())
        {
            if (ball == null || !ball.gameObject.activeInHierarchy) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id != null && id.Type == BallType.Red) count++;
        }
        return count;
    }

    private Rigidbody LowestValueColourOnTable(Rigidbody exclude)
    {
        Rigidbody best = null;
        int bestValue = int.MaxValue;
        foreach (var ball in gameManager.GetBalls())
        {
            if (ball == null || ball == exclude || !ball.gameObject.activeInHierarchy) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id == null || id.Type == BallType.Red || id.Type == BallType.Cue) continue;
            if (GameManager.BallValue.TryGetValue(id.Type, out int value) && value < bestValue)
            {
                bestValue = value;
                best = ball;
            }
        }
        return best;
    }

    // Exactly the balls this shot is legally allowed to hit first, read off the same state the
    // nomination UI reads.
    private void CollectLegalTargets(List<Rigidbody> into)
    {
        into.Clear();

        if (gameManager.CurrentTargetState == GameManager.TargetBallState.Red)
        {
            foreach (var ball in gameManager.GetBalls())
            {
                if (ball == null || !ball.gameObject.activeInHierarchy) continue;
                var id = ball.GetComponent<BallIdentity>();
                if (id != null && id.Type == BallType.Red) into.Add(ball);
            }
            return;
        }

        if (gameManager.CurrentTargetColour.HasValue)
        {
            BallType wanted = gameManager.CurrentTargetColour.Value;
            foreach (var ball in gameManager.GetBalls())
            {
                if (ball == null || !ball.gameObject.activeInHierarchy) continue;
                var id = ball.GetComponent<BallIdentity>();
                if (id != null && id.Type == wanted) into.Add(ball);
            }
            return;
        }

        if (gameManager.NeedsColourNomination)
        {
            // Nothing nominated yet and reds remain, so every colour is a legal choice - the AI picks
            // its shot first and nominates whichever colour that shot is on.
            foreach (var ball in gameManager.GetBalls())
            {
                if (ball == null || !ball.gameObject.activeInHierarchy) continue;
                var id = ball.GetComponent<BallIdentity>();
                if (id != null && id.Type != BallType.Red && id.Type != BallType.Cue) into.Add(ball);
            }
            return;
        }

        // On colour, nothing nominated, reds gone: the fixed sequence runs lowest value first.
        var lowest = LowestValueColourOnTable(null);
        if (lowest != null) into.Add(lowest);
    }

    // ---------------------------------------------------------------- 3. Generating candidates
    private void GenerateCandidates(Vector3 cueBallPos, List<Rigidbody> targets, bool cueBallIsHypothetical, List<ShotCandidate> into)
    {
        into.Clear();
        Rigidbody ignoreCueOnPocketLine = cueBallIsHypothetical ? cueBall : null;

        foreach (var ball in targets)
        {
            if (ball == null) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id == null) continue;

            Vector3 objPos = ball.transform.position;

            for (int pocketIndex = 0; pocketIndex < pockets.Count; pocketIndex++)
            {
                Vector3 pocket = pockets[pocketIndex];
                Vector3 pocketDir = Flat3(pocket - objPos).normalized;
                if (pocketDir.sqrMagnitude < 0.5f) continue;

                Vector3 ghost = objPos - pocketDir * BallDiameter;
                Vector3 toGhost = Flat3(ghost - cueBallPos);
                if (toGhost.sqrMagnitude < 1e-6f) continue;
                Vector3 aimDir = toGhost.normalized;

                float cutAngle = Vector3.Angle(aimDir, pocketDir);
                if (cutAngle > MaxCutAngleDegrees) continue;

                if (!cue.IsPathClear(cueBallPos, ghost, ball, cueBall, true, SightMargin)) continue;
                if (!cue.IsPathClear(objPos, pocket, ball, ignoreCueOnPocketLine, false, SightMargin)) continue;
                // The ball-only check above can't see the jaws. A ball sent at a corner pocket from along
                // the rail hits the jaw and never reaches the drop, however clear the line to the
                // pocket centre is (measured: a corner only takes balls within about 15 degrees of its
                // diagonal).
                if (!DropsInto(objPos, pocketDir, pocketIndex)) continue;

                float d1 = toGhost.magnitude;
                float d2 = Flat3(pocket - objPos).magnitude;
                float span = d1 + d2;

                into.Add(new ShotCandidate
                {
                    objectBall = ball,
                    type = id.Type,
                    ghost = ghost,
                    pocket = pocket,
                    pocketIndex = pocketIndex,
                    cueBallPos = cueBallPos,
                    aimDir = aimDir,
                    cutAngle = cutAngle,
                    span = span,
                    cueToGhost = d1,
                    objectToPocket = d2,
                    potScore = PotScore(cutAngle, span)
                });
            }
        }
    }

    // ---------------------------------------------------------------- 4. Scoring candidates
    private float PotScore(float cutAngle, float span)
    {
        float difficulty = (cutAngle / MaxCutAngleDegrees) * 0.6f + (span / tableDiagonal) * 0.4f;
        return 1f - Mathf.Clamp01(difficulty);
    }

    private float PowerFor(float span)
        => Mathf.Clamp(basePowerFraction + (span / tableDiagonal) * powerPerTableDiagonal,
                       powerFractionLimits.x, powerFractionLimits.y);

    // A pot has to be struck hard enough for the OBJECT ball to reach the pocket, and on a cut it only
    // leaves at the impact speed times cos(cut). PowerFor scales with distance alone, so cut pots were
    // under-hit: logged misses left at 0.85-2.6 m/s for 1.6-5.7 units and stopped 1.3-2.7 short of
    // the pocket. Object balls were measured losing about 2.1 (m/s)^2 per unit rolled; this asks for
    // enough to get there with a unit and a half to spare, works back through the cut and the cue
    // ball's own fall-off (3.6 per unit - over eight units it lost ~3.4, more than short shots show),
    // and leaves headroom for this level's worst under-hit so a power error alone doesn't stop it short.
    private float PotPowerFor(ShotCandidate c)
    {
        float objectSpeedSq = 2.1f * (c.objectToPocket + 1.5f);
        // Confirmed bug (September 2026): this floor was well above cos(MaxCutAngleDegrees) = cos(85) =
        // 0.087, so every candidate this thin (a real, legal, sub-85deg cut - not a hypothetical) had its
        // needed impact speed silently computed off 0.35 instead of its own actual cosine. At 75deg that's
        // a 0.17 power-fraction shortfall, at 80deg 0.50, at 85deg over 1.0 - the exact "tries to pot, runs
        // out of speed and stops near the jaw" symptom, and it landed hardest on exactly the thin/high-cut
        // shots already measured to fail most (see AI_SHOT_SELECTION.md). The floor only needs to stop a
        // literal division blow-up as cutAngle approaches 90 (never reachable - candidates are already
        // rejected past MaxCutAngleDegrees), not shave real power off every legal thin cut.
        float cos = Mathf.Max(0.05f, Mathf.Cos(c.cutAngle * Mathf.Deg2Rad));
        float impactSq = objectSpeedSq / (cos * cos);
        float atOneUnitSq = impactSq + 3.6f * Mathf.Max(0f, c.cueToGhost - 1f);

        float needed = Mathf.Sqrt(atOneUnitSq) / 21.6f * (1f + profile.powerErrorPercent * 0.01f);
        // Paced off what the pot needs alone, not PowerFor: tying pots to basePowerFraction meant the only
        // way to firm them up was to inflate it, which also blasts every safety and escape. Pots measured
        // at spec power were never short; the misses were pots struck too hard (below).
        return Mathf.Clamp(needed, powerFractionLimits.x, Mathf.Max(powerFractionLimits.x, ControlledPotPowerCeiling(c.cueToGhost, needed)));
    }

    // The collision stops being predictable once the cue ball arrives much faster than the throw fit was
    // measured at (up to 6.7 m/s). From 137 logged pots on cluster tables: under 6 m/s the object ball left
    // within a median 0.4-0.8 degrees of the plan and none failed; at 6-8 m/s 4 of 25 failed, 8-11 5 of 20,
    // and above 11 m/s the median launch error was 3.6 degrees and 14 of 52 failed - with no ball touched
    // first, so the extra pace itself was sending the ball off line. The ceiling keeps pot pace (position
    // play's extra pace included) at or under that speed, allowing for this level's worst over-hit. A pot
    // that genuinely needs more still gets it; makeability then judges it at that pace.
    private const float MaxControlledImpactSpeed = 6.5f;

    private float ControlledPotPowerCeiling(float cueToGhost, float needed)
    {
        // Inverse of ImpactSpeed for a firm stroke (speed squared falling ~4 per unit).
        float atOneUnit = Mathf.Sqrt(MaxControlledImpactSpeed * MaxControlledImpactSpeed + 4f * Mathf.Max(0f, cueToGhost - 1f));
        float ceiling = atOneUnit / 21.6f / (1f + profile.powerErrorPercent * 0.01f);
        return Mathf.Min(powerFractionLimits.y, Mathf.Max(ceiling, needed));
    }

    // A safety is soft, but it still has to arrive - a cue ball that stops short of the ball on is a
    // foul, not a safety, so the softness scales the distance-based power rather than replacing it.
    private float SafetyPowerFor(float distanceToContact)
        => Mathf.Clamp(PowerFor(distanceToContact) * safetyPowerMultiplier,
                       powerFractionLimits.x, powerFractionLimits.y);

    // 5.1 - a cheap guess at where the cue ball stops, good enough to rank candidates against each
    // other. The real physics engine decides what actually happens once the shot is struck.
    // Fitted to 18 real strikes (cut 0/30/60, spin 0/+0.5/-0.5, power 0.18/0.28): the cue ball goes
    // off along the tangent about 0.5 x (sideways speed)^2, topspin carries it ~1.7x further and screw
    // ~0.7x, and it runs on along the object ball's line ~0.5 plus or minus what the spin adds. The
    // previous guess put a 60 degree cut's cue ball 0.9 units away when it really travelled 4.6, so
    // every spin choice looked like the same leave and position play did nothing.
    private Vector3 ApproximateCueRest(ShotCandidate candidate, Vector2 spin, float powerFraction)
    {
        Vector3 objectDir = Flat3(candidate.pocket - candidate.ghost).normalized;
        Vector3 tangent = cue.CueDirectionAfterContact(candidate.aimDir, objectDir);

        float impact = ImpactSpeed(powerFraction, candidate.cueToGhost);
        float cut = candidate.cutAngle * Mathf.Deg2Rad;

        float sideways = impact * Mathf.Sin(cut);
        float spinCarry = spin.y >= 0f ? 1f + 1.4f * spin.y : 1f + 0.66f * spin.y;
        float tangentTravel = 0.5f * sideways * sideways * spinCarry;

        float onward = impact * Mathf.Cos(cut);
        float forwardTravel = 0.5f + spin.y * (spin.y >= 0f ? 0.11f : 0.06f) * onward * onward;

        Vector3 rest = candidate.ghost + tangent * tangentTravel + objectDir * forwardTravel;
        return cue.ClampToCushion(candidate.ghost, rest);
    }

    private void CollectNextTargets(Rigidbody potted, List<Rigidbody> into)
    {
        into.Clear();
        var id = potted != null ? potted.GetComponent<BallIdentity>() : null;
        if (id == null) return;

        if (id.Type == BallType.Red)
        {
            foreach (var ball in gameManager.GetBalls())
            {
                if (ball == null || !ball.gameObject.activeInHierarchy) continue;
                var other = ball.GetComponent<BallIdentity>();
                if (other != null && other.Type != BallType.Red && other.Type != BallType.Cue) into.Add(ball);
            }
            return;
        }

        if (RedsRemaining() > 0)
        {
            // A potted colour respots while reds remain, so the next ball on is a red again.
            foreach (var ball in gameManager.GetBalls())
            {
                if (ball == null || !ball.gameObject.activeInHierarchy) continue;
                var other = ball.GetComponent<BallIdentity>();
                if (other != null && other.Type == BallType.Red) into.Add(ball);
            }
            return;
        }

        var next = LowestValueColourOnTable(potted);
        if (next != null) into.Add(next);
    }

    // How good a shot the given resting spot leaves on the next ball on.
    private float NextShotQuality(Vector3 restPos, Rigidbody potted)
    {
        CollectNextTargets(potted, nextTargetBuffer);
        if (nextTargetBuffer.Count == 0) return 0f;

        nextTargetBuffer.Sort((a, b) =>
            Flat3(a.transform.position - restPos).sqrMagnitude
            .CompareTo(Flat3(b.transform.position - restPos).sqrMagnitude));

        // Six covers every colour after a red - the nearest three were often all unpottable.
        int consider = Mathf.Min(6, nextTargetBuffer.Count);
        float best = 0f;

        for (int i = 0; i < consider; i++)
        {
            Rigidbody next = nextTargetBuffer[i];
            Vector3 objPos = next.transform.position;
            for (int pocketIndex = 0; pocketIndex < pockets.Count; pocketIndex++)
            {
                Vector3 pocket = pockets[pocketIndex];
                Vector3 pocketDir = Flat3(pocket - objPos).normalized;
                if (pocketDir.sqrMagnitude < 0.5f) continue;

                Vector3 ghost = objPos - pocketDir * BallDiameter;
                Vector3 toGhost = Flat3(ghost - restPos);
                if (toGhost.sqrMagnitude < 1e-6f) continue;

                float cut = Vector3.Angle(toGhost.normalized, pocketDir);
                if (cut > MaxCutAngleDegrees) continue;

                float d1 = toGhost.magnitude;
                float d2 = Flat3(pocket - objPos).magnitude;
                float score = PotScore(cut, d1 + d2);
                // Cheapest rejections first - this runs for every spin, pace and sample of every survey.
                if (score <= best) continue;
                // Position on a ball that can't physically drop in this pocket is no position at all.
                if (!DropsInto(objPos, pocketDir, pocketIndex)) continue;
                if (!cue.IsPathClear(restPos, ghost, next, cueBall, true, SightMargin)) continue;

                // Only credit a leave this level could actually convert - a "good looking" next ball it
                // would miss is how a visit ends at one pot.
                var shot = new ShotCandidate
                {
                    objectBall = next, pocket = pocket, pocketIndex = pocketIndex, cueBallPos = restPos,
                    aimDir = toGhost.normalized, cutAngle = cut, span = d1 + d2,
                    cueToGhost = d1, objectToPocket = d2, potScore = score
                };
                shot.powerFraction = PotPowerFor(shot);
                CompensateForThrow(ref shot);
                if (IsMakeableAtThisSkill(shot)) best = score;
            }
        }
        return best;
    }

    // How good a leave this spin and pace give, allowing for the rest estimate being off. In play the
    // cue ball stopped 0.1-2.2 units from the prediction on most pots and up to 5 on firm long ones,
    // and a leave that is only good at exactly the predicted spot was the usual way a visit ended
    // (predicted a makeable colour, arrived with none). Averaging the predicted spot with 30% shorter
    // and 30% longer travel favours leaves that survive the error.
    private float LeaveQuality(ShotCandidate candidate, Vector2 spin, float power)
    {
        Vector3 rest = ApproximateCueRest(candidate, spin, power);
        Vector3 travel = Flat3(rest - candidate.ghost) * 0.3f;

        float total = NextShotQuality(rest, candidate.objectBall);
        if (travel.sqrMagnitude < 0.01f) return total;

        total += NextShotQuality(cue.ClampToCushion(rest, rest + travel), candidate.objectBall);
        total += NextShotQuality(rest - travel, candidate.objectBall);
        return total / 3f;
    }

    // Extra pace a positional level may put on a pot purely to send the cue ball further. The pot's own
    // makeability is re-checked at each pace, since harder hits throw more.
    private static readonly float[] PositionPowerScales = { 1f, 1.3f, 1.6f };

    // Picks the spin - and, for levels that play position, the pace - that leaves the best next shot.
    // Writes the chosen power back into the candidate.
    private Vector2 ChooseSpin(ref ShotCandidate candidate, out float positionScore)
    {
        Vector2[] options = profile.deliberateSpinUsage == SpinUsage.Full ? FullSpins
                          : profile.deliberateSpinUsage == SpinUsage.Basic ? BasicSpins
                          : null;

        if (options == null)
        {
            // deliberateSpinUsage None: dead centre, always. Any spin-like result is a mis-hit.
            positionScore = profile.positionWeight > 0f
                ? NextShotQuality(ApproximateCueRest(candidate, Vector2.zero, candidate.powerFraction), candidate.objectBall)
                : 0f;
            return Vector2.zero;
        }

        float basePower = candidate.powerFraction;
        Vector2 best = Vector2.zero;
        float bestPower = basePower;
        float bestScore = float.NegativeInfinity;
        float paceCeiling = ControlledPotPowerCeiling(candidate.cueToGhost, basePower);
        float lastPower = -1f;
        foreach (float scale in PositionPowerScales)
        {
            float power = Mathf.Clamp(basePower * scale, powerFractionLimits.x, paceCeiling);
            // Once the ceiling caps the extra pace, the next scale is the same shot again.
            if (power <= lastPower + 0.005f) continue;
            lastPower = power;
            foreach (var spin in options)
            {
                // Side spin bends the cue ball's path (squirt/swerve) and nothing here models that yet:
                // on Pro, pots played with side missed 19 of 27, sending the object ball 3-41 degrees off,
                // against 2 of 67 without it. Until it is measured and allowed for, pots use only
                // follow/stun/screw.
                if (spin.x != 0f) continue;

                if (scale > 1f)
                {
                    ShotCandidate paced = candidate;
                    paced.powerFraction = power;
                    paced.spin = spin;
                    CompensateForThrow(ref paced);
                    if (!IsMakeableAtThisSkill(paced)) continue;
                }

                float score = LeaveQuality(candidate, spin, power);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = spin;
                    bestPower = power;
                }
            }
        }
        candidate.powerFraction = bestPower;
        positionScore = Mathf.Max(0f, bestScore);
        return best;
    }

    // Can a ball leaving 'from' along 'dir' actually drop into this pocket? Its line has to come within
    // the pocket's reach of the trigger centre, and the ball - most of its width, not just its centre
    // line - has to get that far without hitting a jaw or cushion. Other balls are the line-of-sight
    // checks' job, not this one's.
    private bool DropsInto(Vector3 from, Vector3 dir, int pocketIndex)
    {
        Vector3 toPocket = Flat3(pockets[pocketIndex] - from);
        float along = Vector3.Dot(toPocket, dir);
        float reach = pocketReach[pocketIndex];
        float offLineSq = toPocket.sqrMagnitude - along * along;
        if (along <= 0f || offLineSq >= reach * reach) return false;

        float entryDistance = along - Mathf.Sqrt(reach * reach - offLineSq);
        if (entryDistance <= 0f) return true;

        // 0.6 of the real radius, because a ball that only grazes a jaw is usually knocked in rather
        // than out. Calibrated against 80 rolled balls (6 pockets x approach angle, plus lateral
        // offsets): 0.6 agreed with 73, and never called a ball in that actually stayed out; the full
        // radius agreed with only 64 and rejected corner pots that drop from 0.25 off the line.
        int count = Physics.SphereCastNonAlloc(from, cue.CueBallRadius * 0.6f, dir, entryHits, entryDistance,
                                               ballBlockerMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Rigidbody rb = entryHits[i].collider.attachedRigidbody;
            if (rb != null && rb.GetComponent<BallIdentity>() != null) continue;
            return false;
        }
        return true;
    }

    // Collision throw on this table, measured through the real strike pipeline with zero aim error: the
    // object ball does not leave along the line of centres but is dragged a few degrees towards the cue
    // ball's direction of travel. It grows with cut angle and with impact speed (26 shots, power
    // 0.15-0.32, 1-5 units: e.g. 20/40/60 degree cuts threw 1.4/1.5/1.6 at 2.3 m/s and 3.0/4.6/5.9 at
    // 4.6 m/s), and topspin adds to it. It comes from the physics contact offset, so it is really
    // there for every shot, and 3-4 degrees is 0.2-0.3 units off line four units out - more than a
    // corner's jaws accept. A human player learns to cut a touch thinner for it; this is the AI doing
    // the same (on 12 check shots it took the zero-aim-error object ball from 3.4 to 1.1 degrees off).
    private static float ExpectedThrowDegrees(float cutDegrees, float impactSpeed, float spinY, float cueToContact)
    {
        float cutShape = (0.5f + cutDegrees / 40f) * Mathf.Clamp01(cutDegrees / 10f);
        float speedScale = Mathf.Clamp(0.95f * (impactSpeed - 1.1f), 1f, 3.1f);
        float topspin = 1f + 0.6f * Mathf.Max(0f, spinY) * Mathf.Clamp01((cutDegrees - 15f) / 45f);
        // A cue ball that meets the object ball within a unit or so of being struck is still sliding
        // and throws it about half as much (1-unit calibration shots: 1.5-2.4 vs ~3 further out).
        float sliding = Mathf.Lerp(0.6f, 1f, Mathf.InverseLerp(0.8f, 2.5f, cueToContact));
        return cutShape * speedScale * topspin * sliding;
    }

    // Cue ball speed arriving at the object ball. Power fraction to launch speed and the fall-off per
    // unit travelled are both read off the same calibration shots (1 unit: 3.3 m/s at 0.15 power,
    // 5.4 at 0.25, 6.7 at 0.32; speed squared dropping about 4 per unit for firm shots, 2.6 for soft).
    private static float ImpactSpeed(float powerFraction, float distance)
    {
        float atOneUnit = 21.6f * powerFraction;
        float fallOff = Mathf.Lerp(2.6f, 4f, Mathf.InverseLerp(4f, 5.4f, atOneUnit));
        float squared = atOneUnit * atOneUnit - fallOff * (distance - 1f);
        return Mathf.Max(0.4f * atOneUnit, Mathf.Sqrt(Mathf.Max(0f, squared)));
    }

    private float ThrowFor(ShotCandidate c)
        => ExpectedThrowDegrees(c.cutAngle, ImpactSpeed(c.powerFraction, c.cueToGhost), c.spin.y, c.cueToGhost);

    // Where the object ball actually goes when the cue ball is sent along 'aimDir': the ideal
    // line-of-centres direction, pulled towards the cue ball's travel by the expected throw.
    private bool ObjectDirectionWithThrow(Vector3 from, Vector3 aimDir, Vector3 objPos, float throwDegrees, out Vector3 objectDir)
    {
        if (!IdealObjectDirection(from, aimDir, objPos, out objectDir)) return false;
        float side = Mathf.Sign(Vector3.SignedAngle(objectDir, aimDir, Vector3.up));
        objectDir = Quaternion.AngleAxis(side * throwDegrees, Vector3.up) * objectDir;
        return true;
    }

    // Aim so that, after throw, the object ball runs down the pocket line: pick the ghost ball for a
    // line of centres rotated away from the cue ball's side by the throw - a slightly thinner cut.
    private void CompensateForThrow(ref ShotCandidate c)
    {
        Vector3 objPos = Flat3(c.objectBall.position);
        Vector3 pocketDir = Flat3(c.pocket - objPos).normalized;
        float side = Mathf.Sign(Vector3.SignedAngle(pocketDir, c.aimDir, Vector3.up));
        Vector3 lineOfCentres = Quaternion.AngleAxis(-side * ThrowFor(c), Vector3.up) * pocketDir;

        Vector3 ghost = objPos - lineOfCentres * BallDiameter;
        Vector3 toGhost = Flat3(ghost - c.cueBallPos);
        if (toGhost.sqrMagnitude > 1e-6f) c.aimDir = toGhost.normalized;
    }

    // Would this level's own aim error usually pot it? Swing the aim both ways by a share of the error
    // this shot draws from, follow each through the contact (a cut multiplies the error - a 60 degree
    // cut from five units turns a tenth of a degree into about 2.5 on the object ball) and the throw,
    // and require both to drop past the real jaws. Errors past that share may not pot, so a tight pot
    // is still missed some of the time and the golden rule shows up at the table.
    // The old version measured the miss against the whole pocket trigger (0.48 either side) and
    // ignored both the jaws and the cut's amplification, so it admitted long cut pots the AI could
    // never convert and visits died on them.
    private bool IsMakeableAtThisSkill(ShotCandidate c)
    {
        Vector2 range = profile.AimErrorRangeFor(c.potScore);
        float gateError = ((range.x + range.y) * 0.5f + profile.aimErrorDegreesPerBallPotted * ballsPottedThisVisit)
                         * PressureMultiplier() * profile.makeabilityErrorFraction;

        // Real height for the jaw cast - a flattened position would sweep along the floor plane.
        Vector3 objPos = c.objectBall.position;
        float throwDegrees = ThrowFor(c);
        // The throw correction is a fit, not an oracle. Aim-error sensitivity is modelled exactly
        // (measured -19.9 deg per degree against -20.05 predicted, 45 degree cut from 5 units), but
        // what is left of the throw still scatters the object ball, typically by 0.4-2 degrees on a
        // 45 degree cut. Carry that as extra object-ball error in the same direction as the aim error,
        // which steers the AI towards straighter pots where there is little to throw. (0.6x was tried
        // and left 6 of 11 visits with nothing makeable on - the misses it was meant to stop turned out
        // to be under-hit balls, fixed in PotPowerFor.)
        float throwUncertainty = (0.2f + 0.4f * throwDegrees) * profile.makeabilityErrorFraction;

        for (float sign = -1f; sign <= 1f; sign += 2f)
        {
            Vector3 aim = Quaternion.AngleAxis(sign * gateError, Vector3.up) * c.aimDir;
            if (!ObjectDirectionWithThrow(Flat3(c.cueBallPos), aim, Flat3(objPos), throwDegrees, out Vector3 objectDir)) return false;
            // Same side as the aim error pushed it.
            Vector3 toPocket = Flat3(c.pocket - objPos).normalized;
            float pushedSide = Mathf.Sign(Vector3.SignedAngle(toPocket, objectDir, Vector3.up));
            objectDir = Quaternion.AngleAxis(pushedSide * throwUncertainty, Vector3.up) * objectDir;
            if (!DropsInto(objPos, objectDir, c.pocketIndex)) return false;

            // A couple of degrees off line is still a pot on an empty table, but not if it clips a ball
            // sitting just beside the pocket line, which the dead-straight sight check can't see.
            Vector3 alongPath = objPos + objectDir * c.objectToPocket;
            if (!cue.IsPathClear(objPos, alongPath, c.objectBall, cueBall, false)) return false;
        }
        return true;
    }

    private bool ChooseBestCandidate(out ShotCandidate best)
    {
        best = default;
        candidatesGenerated = candidates.Count;
        if (candidates.Count == 0) return false;

        // Power and throw first (spin is chosen later, so makeability judges the plain-ball throw), then
        // drop the ones this level physically cannot convert before ranking the rest.
        for (int i = 0; i < candidates.Count; i++)
        {
            ShotCandidate c = candidates[i];
            c.powerFraction = PotPowerFor(c);
            CompensateForThrow(ref c);
            c.makeable = IsMakeableAtThisSkill(c);
            candidates[i] = c;
        }

        if (profile.PlaysDeliberateSafety)
        {
            // A level that weighs safety against the pot only takes on pots it can usually convert.
            candidates.RemoveAll(c => !c.makeable);
            if (candidates.Count == 0) return false;
        }
        // A level that never plays safe (Beginner: "always just goes for the ball on, even when that's a
        // bad idea") still goes for the easiest pot it sees - makeability only puts the ones it can
        // actually make first, so it misses the hard ones naturally instead of tapping the ball on.
        candidates.Sort((a, b) => a.makeable != b.makeable
            ? b.makeable.CompareTo(a.makeable)
            : b.potScore.CompareTo(a.potScore));

        int survey = profile.candidateSurveyCount <= 0
            ? candidates.Count
            : Mathf.Min(profile.candidateSurveyCount, candidates.Count);

        float bestScore = float.NegativeInfinity;
        // Debug-only (see the SURVEY log below): the single easiest raw pot in this survey,
        // tracked separately from finalScore's winner, so a temporary audit can tell whether
        // position weighting ever picks a harder pot over an easier one on offer.
        float easiestPotScore = float.NegativeInfinity;
        float easiestPotCut = 0f;
        for (int i = 0; i < survey; i++)
        {
            ShotCandidate c = candidates[i];
            c.spin = ChooseSpin(ref c, out float positionScore);
            c.positionScore = positionScore;

            // A real player refines which GOOD pot to take for position, but doesn't sacrifice a
            // pot they're actually confident in for a much harder one just because it leaves a nicer
            // look at the next ball - measured live, the plain weighted sum had no such safeguard
            // (21% of decisions picked a meaningfully harder pot than the easiest on offer, some
            // trading a ~4deg cut for a 70+deg one purely on position score). Scale position's
            // influence down as the pot itself gets harder, using the same easy/hard pot-score scale
            // this profile already defines for aim error (AimErrorRangeFor) - full weight on an easy
            // pot (>= easyPotScore), fading to none on a hard one (<= hardPotScore), so position still
            // decides between comparably good pots but can no longer justify a drastically worse one.
            float potHardness = Mathf.Clamp01(Mathf.InverseLerp(profile.easyPotScore, profile.hardPotScore, c.potScore));
            c.finalScore = c.potScore * profile.PotWeight + c.positionScore * profile.positionWeight * (1f - potHardness);

            if (c.potScore > easiestPotScore)
            {
                easiestPotScore = c.potScore;
                easiestPotCut = c.cutAngle;
            }

            if (c.finalScore > bestScore)
            {
                bestScore = c.finalScore;
                best = c;
            }
        }

        if (debugLogging && survey > 1)
            Debug.Log($"[AI:{profile.name}] SURVEY candidates={survey}/{candidates.Count} " +
                      $"chosen(pot={best.potScore:F2} pos={best.positionScore:F2} final={best.finalScore:F2} cut={best.cutAngle:F0}) " +
                      $"easiestOnOffer(pot={easiestPotScore:F2} cut={easiestPotCut:F0}) " +
                      $"pickedHarderPot={(best.potScore < easiestPotScore - 0.02f)}");

        // Re-aim for the spin actually chosen - topspin throws the object ball further.
        if (best.objectBall != null) CompensateForThrow(ref best);
        return true;
    }

    // ---------------------------------------------------------------- 4b. Foul play/play-again
    // FOUL_PLAY_AGAIN_RULE.md section 4: when a human foul leaves this AI to decide, wait the same
    // thinking pause as a normal shot, then choose Play if a genuinely makeable pot exists (the
    // exact same candidate-generation/makeability pipeline and minAcceptablePotScore threshold a
    // normal turn already uses to pick pot vs safety), otherwise send the fouling player back in.
    private IEnumerator DecideFoulPlayOrAgain()
    {
        yield return new WaitForSeconds(Random.Range(thinkingDelaySeconds.x, thinkingDelaySeconds.y));
        if (!gameManager.IsAwaitingFoulDecision || gameManager.CurrentPlayerIndex != aiPlayerIndex) yield break;

        gameManager.SetAiActing(true);
        bool play = HasMakeablePotAvailable();
        if (debugLogging)
            Debug.Log($"[AI:{profile.name}] FOUL DECISION: " +
                      $"{(play ? "PLAY (a makeable pot exists)" : "MAKE OPPONENT PLAY AGAIN (nothing makeable)")}");
        if (play) gameManager.ChooseFoulPlay();
        else gameManager.ChooseFoulPlayAgain();
        gameManager.SetAiActing(false);
    }

    // Same "is there a pot here" read the AI does at the start of every normal turn
    // (ChooseShot/ChooseBestCandidate), consulted one step earlier, before committing to actually
    // playing. No new difficulty-specific tuning: a stronger level naturally chooses Play more
    // often simply because it finds makeable pots more often.
    private bool HasMakeablePotAvailable()
    {
        Vector3 cueBallPos = cueBall.transform.position;
        CollectLegalTargets(targetBuffer);
        if (targetBuffer.Count == 0) return false;

        GenerateCandidates(cueBallPos, targetBuffer, false, candidates);
        if (!ChooseBestCandidate(out ShotCandidate best)) return false;
        return best.potScore >= profile.minAcceptablePotScore;
    }

    // ---------------------------------------------------------------- 5. Pot or safety?
    // Wrapper so the candidate count is stamped once for the debug line, rather than at every one of
    // the ten places a plan can be returned from.
    private ShotPlan PlanShot()
    {
        candidates.Clear();
        candidatesGenerated = 0;
        ShotPlan plan = ChooseShot();
        plan.candidatesGenerated = candidatesGenerated;
        plan.candidateCount = 0;
        foreach (var c in candidates)
            if (c.makeable) plan.candidateCount++;
        return plan;
    }

    private ShotPlan ChooseShot()
    {
        Vector3 cueBallPos = cueBall.transform.position;
        CollectLegalTargets(targetBuffer);

        if (targetBuffer.Count == 0)
            return BuildEscape(cueBallPos, "no legal ball on the table");

        GenerateCandidates(cueBallPos, targetBuffer, false, candidates);

        if (!ChooseBestCandidate(out ShotCandidate best))
            return PackIsTight(targetBuffer)
                ? BuildBreak(cueBallPos, targetBuffer)
                : BuildSafety(cueBallPos, targetBuffer, "no makeable pot exists");

        if (best.potScore < profile.minAcceptablePotScore)
            return BuildSafety(cueBallPos, targetBuffer, $"best pot {best.potScore:F2} under {profile.minAcceptablePotScore:F2}");

        // Situational, not a flat global chance: the roll only happens when the best pot on offer is
        // poor enough that a real player would be weighing safety against it.
        if (best.potScore < profile.safetyRollPotScore)
        {
            float chance = profile.RollSafetyProbability();
            if (Random.value < chance)
                return BuildSafety(cueBallPos, targetBuffer, $"safety roll hit ({chance:F2}) on a {best.potScore:F2} pot");
        }

        return new ShotPlan
        {
            aimDir = best.aimDir,
            spin = best.spin,
            powerFraction = best.powerFraction,
            nominate = gameManager.NeedsColourNomination ? best.type : (BallType?)null,
            isSafety = false,
            potScore = best.potScore,
            finalScore = best.finalScore,
            target = $"{best.type} -> pocket {best.pocket.x:F1},{best.pocket.z:F1} " +
                     $"(cut {best.cutAngle:F0}deg, cue->ghost {best.cueToGhost:F2}, obj->pocket {best.objectToPocket:F2}" +
                     $"{(best.makeable ? "" : ", not makeable at this skill")})",
            objectBall = best.objectBall,
            pocket = best.pocket,
            cueBallPos = cueBallPos,
            cueToGhost = best.cueToGhost,
            objectToPocket = best.objectToPocket,
            expectedThrowDegrees = ThrowFor(best),
            predictedCueRest = ApproximateCueRest(best, best.spin, best.powerFraction)
        };
    }

    // Are the balls on still sitting as a rack? A fresh triangle spans well under a couple of ball
    // widths from its own centre; once it has been opened up the spread is many times that.
    private bool PackIsTight(List<Rigidbody> targets)
    {
        if (targets.Count < 4) return false;

        Vector3 centre = Vector3.zero;
        foreach (var ball in targets) centre += Flat3(ball.transform.position);
        centre /= targets.Count;

        float spread = 0f;
        foreach (var ball in targets)
            spread = Mathf.Max(spread, Flat3(ball.transform.position - centre).magnitude);

        return spread < BallDiameter * 4f;
    }

    // The break: nothing is on and the pack is untouched, so hit the reachable ball on firmly enough
    // to actually spread it. Playing the usual soft safety here taps the pack, leaves the opponent the
    // identical position, and the frame never opens at all.
    private ShotPlan BuildBreak(Vector3 cueBallPos, List<Rigidbody> targets)
    {
        Rigidbody target = NearestTo(cueBallPos, targets, out Vector3 aim);
        return new ShotPlan
        {
            aimDir = aim,
            spin = Vector2.zero,
            powerFraction = Mathf.Clamp(breakPowerFraction, powerFractionLimits.x, powerFractionLimits.y),
            nominate = NominationFor(target),
            isSafety = true,
            potScore = 0f,
            target = $"break shot into the pack off {TypeName(target)}"
        };
    }

    private ShotPlan BuildSafety(Vector3 cueBallPos, List<Rigidbody> targets, string reason)
    {
        Rigidbody nearest = NearestTo(cueBallPos, targets, out Vector3 nearestAim);

        if (!profile.PlaysDeliberateSafety)
        {
            // This level never plays a deliberate safety, so its fallback is simply a soft, dead
            // centre-ball contact on the nearest ball on it can legally reach - no hiding, no
            // snooker hunting, just "hit the ball on and hope".
            float reach = nearest != null ? Flat3(nearest.transform.position - cueBallPos).magnitude : tableDiagonal * 0.5f;
            return new ShotPlan
            {
                aimDir = nearestAim,
                spin = Vector2.zero,
                powerFraction = SafetyPowerFor(reach),
                nominate = NominationFor(nearest),
                isSafety = true,
                potScore = 0f,
                target = $"soft contact on {TypeName(nearest)} ({reason})"
            };
        }

        Vector3 bestAim = Vector3.zero;
        Rigidbody bestBall = null;
        float bestScore = float.NegativeInfinity;
        float bestPower = SafetyPowerFor(tableDiagonal * 0.5f);

        foreach (var ball in targets)
        {
            if (ball == null) continue;
            Vector3 objPos = ball.transform.position;
            Vector3 toBall = Flat3(objPos - cueBallPos);
            if (toBall.sqrMagnitude < 1e-6f) continue;
            Vector3 approach = toBall.normalized;

            foreach (float contact in SafetyContacts)
            {
                Vector3 ghost = ContactGhost(objPos, approach, contact);

                Vector3 aimDir = Flat3(ghost - cueBallPos);
                if (aimDir.sqrMagnitude < 1e-6f) continue;
                aimDir = aimDir.normalized;

                if (!cue.IsPathClear(cueBallPos, ghost, ball, cueBall, true, SightMargin)) continue;

                float power = SafetyPowerFor(Flat3(ghost - cueBallPos).magnitude);
                Vector3 objectDir = Flat3(objPos - ghost).normalized;
                Vector3 tangent = cue.CueDirectionAfterContact(aimDir, objectDir);
                float energyKept = Mathf.Abs(contact);
                Vector3 rest = cue.ClampToCushion(ghost, ghost + tangent * (power * tableDiagonal * 0.45f * energyKept));

                float score = SafetyScore(rest, targets);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAim = aimDir;
                    bestBall = ball;
                    bestPower = power;
                }
            }
        }

        if (bestBall == null)
            return BuildEscape(cueBallPos, reason + " / nothing safe reachable");

        return new ShotPlan
        {
            aimDir = bestAim,
            spin = Vector2.zero,
            powerFraction = bestPower,
            nominate = NominationFor(bestBall),
            isSafety = true,
            potScore = 0f,
            target = $"safety off {TypeName(bestBall)} leaving {bestScore:F2} ({reason})"
        };
    }

    // How awkward the opponent's next shot looks from a candidate resting spot: how much of the ball
    // on is hidden from it, plus credit for leaving the cue ball a long way off.
    private float SafetyScore(Vector3 restPos, List<Rigidbody> opponentTargets)
    {
        if (opponentTargets.Count == 0) return 0f;

        int blocked = 0;
        float nearest = float.PositiveInfinity;
        foreach (var ball in opponentTargets)
        {
            if (ball == null) continue;
            Vector3 pos = ball.transform.position;
            if (!cue.IsPathClear(restPos, pos, ball, cueBall, true)) blocked++;
            nearest = Mathf.Min(nearest, Flat3(pos - restPos).magnitude);
        }

        float hidden = blocked / (float)opponentTargets.Count;
        float distance = float.IsPositiveInfinity(nearest) ? 0f : Mathf.Clamp01(nearest / tableDiagonal);
        return hidden + distance * 0.25f;
    }

    // Snookered with nothing clear to play: swing at the ball on anyway and take what comes, same as
    // a player with no escape would.
    private ShotPlan BuildEscape(Vector3 cueBallPos, string reason)
    {
        CollectLegalTargets(targetBuffer);
        Rigidbody nearest = NearestTo(cueBallPos, targetBuffer, out Vector3 aim);
        if (nearest == null) aim = Flat3(pockets[0] - cueBallPos).normalized;

        return new ShotPlan
        {
            aimDir = aim,
            spin = Vector2.zero,
            powerFraction = Mathf.Clamp(0.3f, powerFractionLimits.x, powerFractionLimits.y),
            nominate = NominationFor(nearest),
            isSafety = true,
            potScore = 0f,
            target = $"escape at {TypeName(nearest)} ({reason})"
        };
    }

    private BallType? NominationFor(Rigidbody ball)
    {
        if (!gameManager.NeedsColourNomination || ball == null) return null;
        var id = ball.GetComponent<BallIdentity>();
        return id != null ? id.Type : (BallType?)null;
    }

    // The ghost-ball position for clipping this ball at the given impact parameter: 0 is a full-ball
    // hit, +/-1 is the thinnest possible edge contact.
    private Vector3 ContactGhost(Vector3 objPos, Vector3 approach, float impact)
    {
        Vector3 side = Vector3.Cross(Vector3.up, approach);
        return objPos
             - approach * (BallDiameter * Mathf.Sqrt(Mathf.Max(0f, 1f - impact * impact)))
             + side * (BallDiameter * impact);
    }

    // Whether any legal contact on this ball can be reached from here, and the aim that gets there.
    // Thickest contact first, so the AI only resorts to a thin clip when nothing fuller is on.
    private bool TryFindContact(Vector3 from, Rigidbody ball, out Vector3 aimDir)
    {
        aimDir = Vector3.zero;
        if (ball == null) return false;

        Vector3 objPos = ball.transform.position;
        Vector3 toBall = Flat3(objPos - from);
        if (toBall.sqrMagnitude < 1e-6f) return false;
        Vector3 approach = toBall.normalized;

        foreach (float impact in ContactSamples)
        {
            Vector3 ghost = ContactGhost(objPos, approach, impact);
            Vector3 toGhost = Flat3(ghost - from);
            if (toGhost.sqrMagnitude < 1e-6f) continue;
            if (!cue.IsPathClear(from, ghost, ball, cueBall, true, SightMargin)) continue;

            aimDir = toGhost.normalized;
            return true;
        }
        return false;
    }

    // Nearest legal ball the cue ball can actually make contact with, falling back to the nearest one
    // overall when everything is screened. Aiming where another ball is in the way means hitting the
    // wrong ball first, which is a foul - so reachability comes before distance.
    private Rigidbody NearestTo(Vector3 point, List<Rigidbody> balls, out Vector3 aimDir)
    {
        Rigidbody bestClear = null, bestAny = null;
        Vector3 clearAim = Vector3.zero;
        float clearDistance = float.PositiveInfinity, anyDistance = float.PositiveInfinity;

        foreach (var ball in balls)
        {
            if (ball == null) continue;
            float d = Flat3(ball.transform.position - point).sqrMagnitude;
            if (d < anyDistance) { anyDistance = d; bestAny = ball; }

            if (d < clearDistance && TryFindContact(point, ball, out Vector3 aim))
            {
                clearDistance = d;
                bestClear = ball;
                clearAim = aim;
            }
        }

        if (bestClear != null) { aimDir = clearAim; return bestClear; }

        aimDir = bestAny != null ? Flat3(bestAny.transform.position - point).normalized : Vector3.forward;
        return bestAny;
    }

    // ---------------------------------------------------------------- 6. Error injection + execute
    private ShotPlan ApplyError(ShotPlan plan)
    {
        float pressure = PressureMultiplier();

        Vector2 range = profile.AimErrorRangeFor(plan.potScore);
        float aimError = Random.Range(range.x, range.y);
        aimError += profile.aimErrorDegreesPerBallPotted * ballsPottedThisVisit;
        aimError *= pressure;
        if (Random.value < 0.5f) aimError = -aimError;

        float powerError = Random.Range(profile.powerErrorPercent * profile.powerErrorFloorFraction,
                                        profile.powerErrorPercent) * pressure;
        if (Random.value < 0.5f) powerError = -powerError;

        plan.aimDir = Quaternion.AngleAxis(aimError, Vector3.up) * plan.aimDir;
        plan.powerFraction = Mathf.Clamp(plan.powerFraction * (1f + powerError * 0.01f),
                                         powerFractionLimits.x, powerFractionLimits.y);
        plan.aimErrorDegrees = aimError;
        plan.powerErrorPercent = powerError;
        return plan;
    }

    private IEnumerator TakeShot()
    {
        ShotPlan plan = ApplyError(PlanShot());

        // Brackets every call below that mutates shared turn state (nomination, confirm, strike) so
        // GameManager can tell the AI's own legitimate calls apart from a human's UI click landing
        // during the AI's turn - see GameManager.SetAiActing/BlockedAsHumanInputDuringAiTurn.
        gameManager.SetAiActing(true);

        if (plan.nominate.HasValue)
            gameManager.OnColourNominated(plan.nominate.Value);

        // Spin has to be set while aim is still free - GameManager refuses SetSpinOffset once confirm
        // locks the shot in, exactly as it does for the player's spin widget.
        gameManager.SetSpinOffset(plan.spin);

        // Aim by putting the stick where a human's drag would have put it. Cue.cs then derives its
        // strike direction from the stick's position exactly as it does for a human shot - the error
        // above is already baked into this angle, so there is no truer aim anywhere in the pipeline.
        cueVisual.SetAzimuth(AzimuthFor(plan.aimDir));
        yield return null;
        yield return null;

        if (debugLogging)
        {
            Vector3 realised = cue.CurrentAimForward;
            if (!plan.isSafety && plan.objectBall != null)
                plan.clearance = DescribeClearance(plan);
            Debug.Log($"[AI:{profile.name}] {(plan.isSafety ? "SAFETY" : "POT")} {plan.target} | " +
                      $"{(plan.clearance != null ? plan.clearance + " | " : "")}" +
                      $"on={gameManager.CurrentTargetState}" +
                      $"{(gameManager.CurrentTargetColour.HasValue ? "/" + gameManager.CurrentTargetColour.Value : "")} " +
                      $"legalTargets={targetBuffer.Count} candidates={plan.candidatesGenerated} makeable={plan.candidateCount} | " +
                      $"potScore={plan.potScore:F2} finalScore={plan.finalScore:F2} | " +
                      $"aimErr={plan.aimErrorDegrees:+0.00;-0.00}deg powerErr={plan.powerErrorPercent:+0.0;-0.0}% | " +
                      $"power={plan.powerFraction:F2} spin={plan.spin} | " +
                      $"visit={ballsPottedThisVisit}/{softPotCapThisVisit} pressure=x{PressureMultiplier():F2} | " +
                      $"aim intended={plan.aimDir} realised={realised} off by {Vector3.Angle(plan.aimDir, realised):F2}deg");
        }

        gameManager.ConfirmButtonPressed();

        // Same two calls the power slider makes when the player lifts their finger, through the same
        // min/max power range the slider offers them.
        gameManager.SetStrikeForce(Mathf.Lerp(powerSlider.MinPower, powerSlider.MaxPower, plan.powerFraction));
        gameManager.RequestStrike();

        gameManager.SetAiActing(false);

        // Confirm/RequestStrike both silently no-op on a handful of guard conditions (frame just ended,
        // ball-in-hand, a stray recheck of nextplay) - if either did, nothing was actually struck and
        // nothing will ever call HandleShotResolved for this "shot". Only commit to waiting for a
        // result when the strike was genuinely accepted; otherwise leave waitingForShot false so
        // PlayLoop's next tick simply tries the whole shot again instead of hanging forever.
        if (gameManager.IsConfirmMode && gameManager.IsStrikeRequested)
        {
            waitingForShot = true;
            waitingForShotSince = Time.time;
            shotWasMine = true;

            if (debugLogging && !plan.isSafety && plan.objectBall != null)
                StartCoroutine(TrackPotAttempt(plan));
        }
        else if (debugLogging)
        {
            Debug.LogWarning($"[AI:{profile.name}] Strike request was not accepted (confirmMode=" +
                              $"{gameManager.IsConfirmMode}, strikeRequested={gameManager.IsStrikeRequested}) - " +
                              "will retry next loop instead of waiting for a shot that was never taken.");
        }
    }

    // Where the object ball WOULD go on a perfect ghost-ball contact, given the aim actually played
    // (error included): follow the cue ball's line until it is one diameter from the object ball's
    // centre, and the object ball leaves along the line of centres.
    private bool IdealObjectDirection(Vector3 from, Vector3 aimDir, Vector3 objPos, out Vector3 objectDir)
    {
        objectDir = Vector3.zero;
        Vector3 toObj = Flat3(objPos - from);
        float along = Vector3.Dot(toObj, aimDir);
        float perpSq = toObj.sqrMagnitude - along * along;
        float diameterSq = BallDiameter * BallDiameter;
        if (along <= 0f || perpSq >= diameterSq) return false;

        Vector3 contactCentre = from + aimDir * (along - Mathf.Sqrt(diameterSq - perpSq));
        objectDir = Flat3(objPos - contactCentre).normalized;
        return true;
    }

    // Debug only (debugLogging): compares what the object ball really did with what the shot model
    // predicts from the aim actually played - error and throw correction included - plus how close it
    // got to the pocket, how fast it left, and where the cue ball stopped against the position
    // estimate. Agreement means misses are the injected error; disagreement points at the model.
    private IEnumerator TrackPotAttempt(ShotPlan plan)
    {
        Rigidbody ball = plan.objectBall;
        Vector3 objStart = Flat3(ball.position);
        Vector3 pocket = Flat3(plan.pocket);
        Vector3 intended = (pocket - objStart).normalized;

        // Prediction = the aim actually played (error included) through the contact, plus the throw
        // the AI already allowed for. With zero aim error this is ~0 when the throw correction is right.
        string predicted = "cue ball misses the object ball entirely";
        if (ObjectDirectionWithThrow(Flat3(plan.cueBallPos), plan.aimDir, objStart, plan.expectedThrowDegrees, out Vector3 predictedDir))
        {
            float predictedDev = Vector3.SignedAngle(intended, predictedDir, Vector3.up);
            predicted = $"{predictedDev:+0.00;-0.00}deg (misses pocket centre by " +
                        $"{plan.objectToPocket * Mathf.Tan(Mathf.Abs(predictedDev) * Mathf.Deg2Rad):F3}, " +
                        $"throw allowed {plan.expectedThrowDegrees:F2}deg)";
        }

        // Which other ball each moving ball set off first. A resting ball that starts moving right beside
        // the cue ball or the object ball was hit by it - this is what tells a secondary collision apart
        // from a bad contact when the object ball goes somewhere the model didn't predict.
        var contacts = new ContactScan { objectStart = objStart };
        var resting = new List<Rigidbody>();
        foreach (var other in gameManager.GetBalls())
            if (other != null && other != ball && other != cueBall && other.gameObject.activeInHierarchy)
                resting.Add(other);

        float giveUp = Time.time + 20f;
        while (!gameManager.PottedThisShot.Contains(ball) && Flat3(ball.velocity).sqrMagnitude < 0.01f)
        {
            if (Time.time > giveUp || (!waitingForShot && Time.time > giveUp - 18f))
            {
                Debug.Log($"[AI:{profile.name}] TRACK {TypeName(ball)} never moved | predicted objDev {predicted} | " +
                          $"{plan.clearance} | {contacts.Describe()}");
                yield break;
            }
            ScanContacts(ball, resting, ref contacts);
            yield return new WaitForFixedUpdate();
        }

        // A couple of steps in, so the direction is off the contact rather than mid-collision.
        ScanContacts(ball, resting, ref contacts);
        yield return new WaitForFixedUpdate();
        ScanContacts(ball, resting, ref contacts);
        yield return new WaitForFixedUpdate();
        Vector3 v = Flat3(ball.velocity);
        string actual = v.sqrMagnitude > 1e-4f
            ? $"{Vector3.SignedAngle(intended, v.normalized, Vector3.up):+0.00;-0.00}deg at {v.magnitude:F2} m/s"
            : "n/a";

        float closest = float.PositiveInfinity;
        while (!gameManager.PottedThisShot.Contains(ball) && Time.time < giveUp && waitingForShot)
        {
            closest = Mathf.Min(closest, (Flat3(ball.position) - pocket).magnitude);
            ScanContacts(ball, resting, ref contacts);
            yield return new WaitForFixedUpdate();
        }
        bool dropped = gameManager.PottedThisShot.Contains(ball);

        // Position check: where the cue ball actually came to rest against where position play
        // expected it.
        while (waitingForShot && Time.time < giveUp) yield return new WaitForFixedUpdate();
        float restMiss = (Flat3(cueBall.position) - Flat3(plan.predictedCueRest)).magnitude;

        Debug.Log($"[AI:{profile.name}] TRACK {TypeName(ball)} | predicted objDev {predicted} | " +
                  $"actual objDev {actual} | closest to pocket {(dropped ? "DROPPED" : closest.ToString("F3"))} | " +
                  $"cue rest off prediction by {restMiss:F2} | {plan.clearance} | {contacts.Describe()}");
    }

    private struct ContactScan
    {
        public Vector3 objectStart;
        public Rigidbody cueHitFirst;          // a ball the cue ball hit before the object ball moved
        public Rigidbody objectHitFirst;       // the first ball the object ball ran into
        public float objectTravelAtHit;

        public string Describe()
            => $"contacts: cue ball kissed {(cueHitFirst != null ? TypeName(cueHitFirst) : "none")} before the object ball, " +
               $"object ball hit {(objectHitFirst != null ? TypeName(objectHitFirst) + $" after {objectTravelAtHit:F2}" : "none")} on its way";
    }

    // Debug only. A ball that was at rest and is now moving, and sits within half a radius of touching
    // the cue ball or the object ball, was just hit by that ball (at 10 ms steps two balls separate by
    // at most ~0.05 in the step the contact happens).
    private void ScanContacts(Rigidbody ball, List<Rigidbody> resting, ref ContactScan scan)
    {
        float touching = BallDiameter + cue.CueBallRadius * 0.5f;
        bool objectMoving = Flat3(ball.velocity).sqrMagnitude >= 0.01f || gameManager.PottedThisShot.Contains(ball);

        for (int i = resting.Count - 1; i >= 0; i--)
        {
            Rigidbody other = resting[i];
            if (other == null || !other.gameObject.activeInHierarchy) { resting.RemoveAt(i); continue; }
            if (Flat3(other.velocity).sqrMagnitude < 0.0025f) continue;
            resting.RemoveAt(i);

            Vector3 at = Flat3(other.position);
            if (objectMoving && scan.objectHitFirst == null && (at - Flat3(ball.position)).magnitude < touching)
            {
                scan.objectHitFirst = other;
                scan.objectTravelAtHit = (Flat3(ball.position) - scan.objectStart).magnitude;
            }
            else if (!objectMoving && scan.cueHitFirst == null && (at - Flat3(cueBall.position)).magnitude < touching)
            {
                scan.cueHitFirst = other;
            }
        }
    }

    // Debug only. How much room the two shot lines really had, measured straight off the ball centres
    // rather than through a sphere cast: the gap left beside the travelling ball by the nearest other
    // ball ahead of it, in ball radii (0 = grazing, negative = in the way), plus how many balls sit
    // within three ball widths of the object ball - the "is this a cluster shot" figure.
    private string DescribeClearance(ShotPlan plan)
    {
        Vector3 objPos = Flat3(plan.objectBall.position);
        Vector3 from = Flat3(plan.cueBallPos);
        Vector3 contact = objPos - Flat3(plan.pocket - objPos).normalized * BallDiameter;
        if (IdealObjectDirection(from, plan.aimDir, objPos, out Vector3 objectDir))
            contact = objPos - objectDir * BallDiameter;

        float cueGap = LineGap(from, contact, plan.objectBall, out Rigidbody cueNearest);
        float objGap = LineGap(objPos, Flat3(plan.pocket), plan.objectBall, out Rigidbody objNearest);

        int cluster = 0;
        foreach (var other in gameManager.GetBalls())
        {
            if (other == null || other == plan.objectBall || other == cueBall || !other.gameObject.activeInHierarchy) continue;
            if ((Flat3(other.position) - objPos).magnitude < BallDiameter * 3f) cluster++;
        }

        return $"gaps cue->ghost={FormatGap(cueGap, cueNearest)} obj->pocket={FormatGap(objGap, objNearest)} cluster3D={cluster}";
    }

    private static string FormatGap(float gap, Rigidbody nearest)
        => nearest == null ? "open" : $"{gap:+0.00;-0.00}r({TypeName(nearest)})";

    private float LineGap(Vector3 from, Vector3 to, Rigidbody objectBall, out Rigidbody nearest)
    {
        nearest = null;
        Vector3 delta = to - from;
        float length = delta.magnitude;
        if (length < 1e-4f) return float.PositiveInfinity;
        Vector3 dir = delta / length;

        float best = float.PositiveInfinity;
        foreach (var other in gameManager.GetBalls())
        {
            if (other == null || other == objectBall || other == cueBall || !other.gameObject.activeInHierarchy) continue;
            Vector3 p = Flat3(other.position) - from;
            float along = Vector3.Dot(p, dir);
            // Only balls level with the travelling ball's path: from its start to one diameter past the end.
            if (along <= 0f || along > length + BallDiameter) continue;
            float side = (p - dir * Mathf.Min(along, length)).magnitude;
            float gap = (side - BallDiameter) / cue.CueBallRadius;
            if (gap < best) { best = gap; nearest = other; }
        }
        // Anything further than two ball widths off the line is irrelevant to the question.
        if (best > 4f) nearest = null;
        return best;
    }

    // CueVisualController parks the stick at sin/cos of this angle around the ball and points it back
    // at the ball, so this is the azimuth whose resulting aim line is the direction we want.
    private static float AzimuthFor(Vector3 aimDir)
        => Mathf.Atan2(-aimDir.x, -aimDir.z) * Mathf.Rad2Deg;

    // ---------------------------------------------------------------- 7. Ball in hand
    private IEnumerator PlaceCueBall()
    {
        CollectLegalTargets(targetBuffer);

        float y = cueBall.transform.position.y;
        Vector3 open = gameManager.DOpenDirection;
        Vector3 alongBaulk = Vector3.Cross(Vector3.up, open);

        Vector3 bestPoint = gameManager.LastValidPlacement;
        float bestScore = -1f;

        for (int ring = 1; ring <= 3; ring++)
        {
            float radius = gameManager.DRadius * ring / 3.5f;
            for (int step = 0; step <= 6; step++)
            {
                float angle = Mathf.PI * step / 6f;
                Vector3 point = gameManager.DCenter
                              + (alongBaulk * Mathf.Cos(angle) + open * Mathf.Sin(angle)) * radius;
                point.y = y;

                if (!gameManager.IsValidPlacement(point, out _)) continue;

                // Same candidate generation as any other shot - the only difference is that the cue
                // ball isn't standing here yet, so casts have to ignore where it currently sits.
                GenerateCandidates(point, targetBuffer, true, candidates);
                float score = 0f;
                foreach (var candidate in candidates)
                {
                    if (candidate.potScore <= score) continue;
                    ShotCandidate c = candidate;
                    c.powerFraction = PotPowerFor(c);
                    CompensateForThrow(ref c);
                    if (IsMakeableAtThisSkill(c)) score = c.potScore;
                }

                // Off the break there is no pot anywhere in the D, and picking on pot score alone
                // would just take the first sample - which sits behind the blue and pink and can't
                // reach a red at all. Seeing the ball on is worth less than a pot but far more than
                // nothing, so it breaks the tie and sends the AI out to the side of the D.
                score += ReachableFraction(point, targetBuffer) * 0.4f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = point;
                }
            }
            yield return null;
        }

        cueBall.velocity = Vector3.zero;
        cueBall.angularVelocity = Vector3.zero;
        // Rigidbody.position, not transform.position: with Physics.autoSyncTransforms off (the
        // default) a transform write on a non-kinematic body is discarded at the next physics step,
        // which would leave the AI aiming the shot it planned from a spot it isn't standing on.
        cueBall.position = bestPoint;
        cueBall.transform.position = bestPoint;

        gameManager.TryPlaceCueBall(bestPoint);
        gameManager.ConfirmPlacement();

        if (debugLogging)
            Debug.Log($"[AI:{profile.name}] Placed in the D at {bestPoint} - score {bestScore:F2}, " +
                      $"{ReachableFraction(bestPoint, targetBuffer) * 100f:F0}% of the ball on in view.");

        yield return null;
    }

    // How much of the ball on the cue ball could legally make contact with from here.
    private float ReachableFraction(Vector3 from, List<Rigidbody> targets)
    {
        if (targets.Count == 0) return 0f;
        int reachable = 0;
        foreach (var ball in targets)
            if (TryFindContact(from, ball, out _)) reachable++;
        return reachable / (float)targets.Count;
    }

    private static string TypeName(Rigidbody ball)
    {
        var id = ball != null ? ball.GetComponent<BallIdentity>() : null;
        return id != null ? id.Type.ToString() : "nothing";
    }

    private static Vector3 Flat3(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
