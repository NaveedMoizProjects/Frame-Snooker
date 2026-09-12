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
    [Tooltip("Power fraction for a shot with no distance to cover at all.")]
    [SerializeField] private float basePowerFraction = 0.10f;
    [Tooltip("Extra power fraction per full table diagonal the ball has to travel.")]
    [SerializeField] private float powerPerTableDiagonal = 0.35f;
    [Tooltip("Safeties are played at this fraction of the power the same distance would normally get. " +
             "Below about 0.5 the cue ball stops short of the ball on, which is a foul, not a safety.")]
    [SerializeField] private float safetyPowerMultiplier = 0.8f;

    [Tooltip("Power fraction for the break shot - the firm hit into a still-racked pack when there is " +
             "nothing on. A soft safety here just taps the pack and hands back the same position.")]
    [SerializeField] private float breakPowerFraction = 0.65f;
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

    private readonly List<ShotCandidate> candidates = new List<ShotCandidate>(128);
    private readonly List<Rigidbody> targetBuffer = new List<Rigidbody>(16);
    private readonly List<Rigidbody> nextTargetBuffer = new List<Rigidbody>(16);

    private bool waitingForShot;
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
        public Vector3 aimDir;
        public float cutAngle;
        public float span;
        public float potScore;
        public float positionScore;
        public float finalScore;
        public Vector2 spin;
        public float powerFraction;
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

        foreach (var pocket in FindObjectsOfType<PocketTrigger>())
            pockets.Add(pocket.transform.position);

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

    private IEnumerator PlayLoop()
    {
        while (true)
        {
            yield return null;
            if (!MyTurnToAct()) continue;

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
                Debug.Log($"[AI:{profile.name}] Shot resolved, {potted} potted. " +
                          $"Visit now {ballsPottedThisVisit} (soft cap {softPotCapThisVisit}), " +
                          $"error x{PressureMultiplier():F2}.");
        }
        else
        {
            if (debugLogging && visitActive)
                Debug.Log($"[AI:{profile.name}] Visit over after {ballsPottedThisVisit} ball(s).");
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

            foreach (var pocket in pockets)
            {
                Vector3 pocketDir = Flat3(pocket - objPos).normalized;
                if (pocketDir.sqrMagnitude < 0.5f) continue;

                Vector3 ghost = objPos - pocketDir * BallDiameter;
                Vector3 toGhost = Flat3(ghost - cueBallPos);
                if (toGhost.sqrMagnitude < 1e-6f) continue;
                Vector3 aimDir = toGhost.normalized;

                float cutAngle = Vector3.Angle(aimDir, pocketDir);
                if (cutAngle > MaxCutAngleDegrees) continue;

                if (!cue.IsPathClear(cueBallPos, ghost, ball, cueBall, true)) continue;
                if (!cue.IsPathClear(objPos, pocket, ball, ignoreCueOnPocketLine, false)) continue;

                float span = toGhost.magnitude + Flat3(pocket - objPos).magnitude;

                into.Add(new ShotCandidate
                {
                    objectBall = ball,
                    type = id.Type,
                    ghost = ghost,
                    pocket = pocket,
                    aimDir = aimDir,
                    cutAngle = cutAngle,
                    span = span,
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

    // A safety is soft, but it still has to arrive - a cue ball that stops short of the ball on is a
    // foul, not a safety, so the softness scales the distance-based power rather than replacing it.
    private float SafetyPowerFor(float distanceToContact)
        => Mathf.Clamp(PowerFor(distanceToContact) * safetyPowerMultiplier,
                       powerFractionLimits.x, powerFractionLimits.y);

    // 5.1 - a cheap guess at where the cue ball stops, good enough to rank candidates against each
    // other. The real physics engine decides what actually happens once the shot is struck.
    private Vector3 ApproximateCueRest(ShotCandidate candidate, Vector2 spin)
    {
        Vector3 objectDir = Flat3(candidate.pocket - candidate.ghost).normalized;
        Vector3 tangent = cue.CueDirectionAfterContact(candidate.aimDir, objectDir);

        // A full-ball hit dumps nearly everything into the object ball (stun); a thin cut keeps it.
        float energyKept = Mathf.Sin(candidate.cutAngle * Mathf.Deg2Rad);
        float travel = candidate.powerFraction * tableDiagonal * 0.45f * energyKept;

        Vector3 rest = candidate.ghost + tangent * travel;
        // Deliberate screw/follow drags the finish back down or up the original aim line instead.
        rest += candidate.aimDir * (spin.y * candidate.powerFraction * tableDiagonal * 0.2f);

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

        int consider = Mathf.Min(3, nextTargetBuffer.Count);
        float best = 0f;

        for (int i = 0; i < consider; i++)
        {
            Vector3 objPos = nextTargetBuffer[i].transform.position;
            foreach (var pocket in pockets)
            {
                Vector3 pocketDir = Flat3(pocket - objPos).normalized;
                if (pocketDir.sqrMagnitude < 0.5f) continue;

                Vector3 ghost = objPos - pocketDir * BallDiameter;
                Vector3 toGhost = Flat3(ghost - restPos);
                if (toGhost.sqrMagnitude < 1e-6f) continue;

                float cut = Vector3.Angle(toGhost.normalized, pocketDir);
                if (cut > MaxCutAngleDegrees) continue;

                best = Mathf.Max(best, PotScore(cut, toGhost.magnitude + Flat3(pocket - objPos).magnitude));
            }
        }
        return best;
    }

    private Vector2 ChooseSpin(ShotCandidate candidate, out float positionScore)
    {
        Vector2[] options = profile.deliberateSpinUsage == SpinUsage.Full ? FullSpins
                          : profile.deliberateSpinUsage == SpinUsage.Basic ? BasicSpins
                          : null;

        if (options == null)
        {
            // deliberateSpinUsage None: dead centre, always. Any spin-like result is a mis-hit.
            positionScore = profile.positionWeight > 0f
                ? NextShotQuality(ApproximateCueRest(candidate, Vector2.zero), candidate.objectBall)
                : 0f;
            return Vector2.zero;
        }

        Vector2 best = Vector2.zero;
        float bestScore = float.NegativeInfinity;
        foreach (var spin in options)
        {
            float score = NextShotQuality(ApproximateCueRest(candidate, spin), candidate.objectBall);
            if (score > bestScore)
            {
                bestScore = score;
                best = spin;
            }
        }
        positionScore = Mathf.Max(0f, bestScore);
        return best;
    }

    private bool ChooseBestCandidate(out ShotCandidate best)
    {
        best = default;
        if (candidates.Count == 0) return false;

        candidates.Sort((a, b) => b.potScore.CompareTo(a.potScore));

        int survey = profile.candidateSurveyCount <= 0
            ? candidates.Count
            : Mathf.Min(profile.candidateSurveyCount, candidates.Count);

        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < survey; i++)
        {
            ShotCandidate c = candidates[i];
            c.powerFraction = PowerFor(c.span);
            c.spin = ChooseSpin(c, out float positionScore);
            c.positionScore = positionScore;
            c.finalScore = c.potScore * profile.PotWeight + c.positionScore * profile.positionWeight;

            if (c.finalScore > bestScore)
            {
                bestScore = c.finalScore;
                best = c;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- 5. Pot or safety?
    private ShotPlan PlanShot()
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
            target = $"{best.type} -> pocket {best.pocket.x:F1},{best.pocket.z:F1} (cut {best.cutAngle:F0}deg)"
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

                if (!cue.IsPathClear(cueBallPos, ghost, ball, cueBall, true)) continue;

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
            if (!cue.IsPathClear(from, ghost, ball, cueBall, true)) continue;

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
            Debug.Log($"[AI:{profile.name}] {(plan.isSafety ? "SAFETY" : "POT")} {plan.target} | " +
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

        waitingForShot = true;
        shotWasMine = true;
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
                    score = Mathf.Max(score, candidate.potScore);

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
