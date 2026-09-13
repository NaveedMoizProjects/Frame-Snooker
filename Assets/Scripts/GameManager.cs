using System;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    // ---------------- Singleton ----------------
    public static GameManager Instance { get; private set; }

    [Header("Game Settings")]
    [SerializeField] private List<Rigidbody> balls; // all balls in the game

    [Header("Phase 2 - Potting")]
    [Tooltip("Must also be present in the Balls list above. Special-cased: never permanently deactivated, just respawned.")]
    [SerializeField] private Rigidbody cueBall;
    [Tooltip("Where the cue ball respawns to if it gets potted (e.g. the 'D' / baulk spot).")]
    [SerializeField] private Transform cueBallRespawnPoint;

    // Balls potted THIS shot - cleared at the start of each new shot, read once the
    // shot finishes (OnAllBallsStopped) by whatever consumes it (Phase 3/4 rules later).
    public List<Rigidbody> PottedThisShot { get; private set; } = new List<Rigidbody>();

    // Phase 4: first ball the cue ball touches this shot - null means it hit nothing
    // (or only cushions). Reported by CueBallContactTracker.cs on the cue ball.
    private Rigidbody firstBallContacted = null;

    // Fires once per potted ball, the instant it goes in - lets other systems (crowd reaction,
    // SFX, future scoring) react without GameManager needing to know about them directly.
    public event Action<Rigidbody> OnBallPottedEvent;

    [Header("Strike Settings")]
    [Tooltip("This is the ONLY force scale now. Cue.cs's own 'forceMultiplier' still applies on top " +
             "of this, so keep that at 1 while tuning, then adjust one of the two - not both at once.")]
    [SerializeField] private float baseStrikeForce = 8f; // was 30 * 30 = 900 (way too high for an Impulse)

    [Header("Ball Rest Thresholds")]
    [Tooltip("Above this speed, a ball counts as 'moving' and blocks the next shot.")]
    [SerializeField] private float moveThreshold = 0.01f;
    [Tooltip("Below this speed, a moving ball is snapped to a full stop to kill physics jitter.")]
    [SerializeField] private float snapToZeroThreshold = 0.09f;

    [Header("Off-Table Safeguard")]
    [Tooltip("Min X/Z of the play area. Anything outside this is treated as 'the cue ball left the table'. " +
             "Defaults are the outer extents of the cushion colliders, so a ball sitting in a pocket jaw " +
             "is still comfortably inside.")]
    [SerializeField] private Vector2 playAreaMin = new Vector2(-5.9f, -8.5f);
    [SerializeField] private Vector2 playAreaMax = new Vector2(5.9f, 8.5f);
    [Tooltip("Y below which the cue ball counts as having fallen off/through the table. Balls currently " +
             "have FreezePositionY so this can't trigger - it's here for if that constraint is ever lifted.")]
    [SerializeField] private float minimumY = 3.0f;

    [Header("Ball in Hand (placement in the D)")]
    [Tooltip("Shown while the player is placing the cue ball in the D: prompt label + a tick button wired to ConfirmPlacement.")]
    [SerializeField] private GameObject placementPanel;

    [Header("TEMP DEBUG - delete after fixing")]
    [SerializeField] private bool debugLogging = true;
    private float debugLogTimer2 = 0f;

    // state
    private bool nextplay = false;
    private bool strikeRequested = false;
    private bool confirmMode = false;
    private bool inputLocked = false;
    private bool frameOver = false;
    private bool awaitingPlacement = false;

    // Set by EvaluateFoul, read by EvaluateShotResult, which runs straight after it. A red potted on
    // a foul shot must NOT advance Red -> Colour: after a foul the incoming player is still on Red
    // while reds remain. Without this the state flipped anyway, and since only a legal colour pot
    // flips it back, the frame got stuck on Colour with reds still on the table.
    private bool lastShotWasFoul = false;

    // Spin: the live dot position from the spin widget, and the value locked in for the shot being
    // played. RequestStrike copies one into the other before resetting the live value, so spin never
    // carries over into the next shot but the strike that's already been requested still gets it.
    private const float MaxSpinRadius = 0.85f;
    private Vector2 spinOffset = Vector2.zero;
    private Vector2 strikeSpin = Vector2.zero;

    // The D, derived at Start from the Green/Brown/Yellow/Black spawn spots.
    private Vector3 dCenter;
    private float dRadius;
    private Vector3 dOpenDirection; // unit XZ vector pointing from the baulk line into the D
    private Vector3 lastValidPlacement;
    private readonly Dictionary<Rigidbody, float> ballRadius = new Dictionary<Rigidbody, float>();

    // Fires once when the frame ends (black potted off the end of the colour sequence).
    // Argument is the winning player index, or -1 for a tie.
    public event Action<int> OnFrameEnded;
    public bool IsFrameOver => frameOver;

    private CameraSwitching cameraSwitching;
    private bool wasMovingLastCheck = false;

    // Event: fires once when balls transition moving -> stopped
    public event Action OnAllBallsStopped;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Duplicate GameManager found - destroying extra.", this);
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // The shot is only truly "over" once every ball has actually come to rest -
        // not the instant force is applied. Re-enabling aim/confirm here (instead of
        // from inside Cue.ApplyForceToCueBall) stops the cue from immediately chasing
        // a ball that's still flying across the table.
        OnAllBallsStopped += () =>
        {
            confirmMode = false;
            inputLocked = false;
            if (debugLogging) Debug.Log("[GMDebug] All balls stopped - confirm/input unlocked for next shot.");
        };

        // Phase 4: foul evaluation MUST run before Phase 3's state transitions below,
        // since it needs to read targetState/currentTargetColour as they were BEFORE
        // this shot (Phase 3's handler mutates them for the *next* shot).
        OnAllBallsStopped += EvaluateFoul;

        // Phase 3: once a shot has fully settled, work out what got potted and apply
        // colour/scoring rules. Runs after the unlock above but order between the two
        // doesn't matter - they touch unrelated state.
        OnAllBallsStopped += EvaluateShotResult;

        // Phase 5: keep the manually-built nomination panel in sync with game state.
        // This replaces ColourNominationUI entirely - GameManager just toggles the panel
        // GameObject directly, no separate UI script involved.
        OnTargetChanged += (state, colour) => RefreshColourNominationPanel();
    }

    void Start()
    {
        cameraSwitching = FindObjectOfType<CameraSwitching>();
        if (cameraSwitching == null)
            Debug.LogWarning("GameManager: No CameraSwitching found in scene.", this);

        Debug.Log($"GameManager initialized: baseStrikeForce={baseStrikeForce} => GetStrikeForce()={GetStrikeForce()}");

        // Set correct initial visibility (starts hidden, since targetState starts on Red).
        RefreshColourNominationPanel();

        // Frame start: the first player places the cue ball in the D before anyone strikes.
        ComputeDGeometry();
        BeginPlacement();
    }

    // Shows the manually-built panel exactly when NeedsColourNomination is true, hides it
    // otherwise. Called from Awake's OnTargetChanged subscription and once at Start.
    private void RefreshColourNominationPanel()
    {
        if (colourNominationPanel != null)
            colourNominationPanel.SetActive(NeedsColourNomination);
    }

    public void Cam1() => cameraSwitching?.SwitchToTopDownCamera();
    public void Cam2() => cameraSwitching?.SwitchToThirdPersonCamera();
    public void Cam3() => cameraSwitching?.SwitchToFirstPersonCamera();

    public bool isNextPlay() => nextplay;

    // Lets other systems (like BallRollingFriction) read the same balls list
    // instead of duplicating it in the Inspector.
    public List<Rigidbody> GetBalls() => balls;

    void FixedUpdate()
    {
        CheckCueBallOffTable();
        CheckNextPlay(balls);
    }

    // Safeguard for the cue ball leaving the playable surface (launched off a rail, squeezed
    // out through a collider seam). Routed through OnBallPotted so it lands on exactly the same
    // penalty path as potting the cue ball: respot + foul + turn passes, scored by EvaluateFoul.
    private void CheckCueBallOffTable()
    {
        // While in hand the ball is being dragged freely and may pass over the rails.
        if (cueBall == null || frameOver || awaitingPlacement) return;
        if (PottedThisShot.Contains(cueBall)) return; // already handled this shot

        Vector3 p = cueBall.transform.position;
        bool outside = p.x < playAreaMin.x || p.x > playAreaMax.x
                    || p.z < playAreaMin.y || p.z > playAreaMax.y
                    || p.y < minimumY;
        if (!outside) return;

        Debug.Log($"[GMDebug] Cue ball left the table at {p} - treating as a foul and respotting.");
        OnBallPotted(cueBall);
    }

    // ----- Ball motion / next-play logic -----
    public void CheckNextPlay(List<Rigidbody> balls)
    {
        bool anyMoving = false;

        if (debugLogging) debugLogTimer2 += Time.fixedDeltaTime;
        bool shouldLogThisTick = debugLogging && debugLogTimer2 > 1f;
        if (shouldLogThisTick) debugLogTimer2 = 0f;

        foreach (Rigidbody ball in balls)
        {
            if (ball == null)
            {
                if (shouldLogThisTick) Debug.LogWarning("[GMDebug] A ball slot in the Balls list is EMPTY/missing!");
                continue;
            }

            // Phase 2: potted balls are pooled (SetActive(false)), not destroyed - skip them
            // entirely so they never block nextplay or get scanned for collisions.
            if (!ball.gameObject.activeInHierarchy) continue;
            if (ball.isKinematic) continue; // cue ball while it's being dragged into the D

            float speed = ball.velocity.magnitude;
            // A ball that has almost stopped translating but still carries screw/follow spin is not
            // at rest - the cloth is about to turn that spin back into motion. Judging rest on linear
            // speed alone froze screw shots dead right after contact.
            float slip = ContactSlip(ball);

            if (speed > moveThreshold || slip > moveThreshold)
            {
                anyMoving = true;

                if (shouldLogThisTick)
                    Debug.Log($"[GMDebug] {ball.gameObject.name} is moving at speed {speed:F4} slip {slip:F4} pos={ball.transform.position}");
            }

            // Also clears vertical-axis side spin left on a resting ball, which produces no slip and
            // would otherwise carry into the next shot.
            if (speed < snapToZeroThreshold && slip < snapToZeroThreshold
                && (ball.velocity != Vector3.zero || ball.angularVelocity != Vector3.zero))
            {
                ball.velocity = Vector3.zero;
                ball.angularVelocity = Vector3.zero;
            }
        }

        nextplay = !anyMoving;

        if (wasMovingLastCheck && !anyMoving)
            OnAllBallsStopped?.Invoke();

        wasMovingLastCheck = anyMoving;
    }

    // Speed of the ball's surface where it touches the cloth - the same quantity BallRollingFriction
    // acts on. Non-zero while the ball slides or spins against the cloth.
    private float ContactSlip(Rigidbody ball)
    {
        Vector3 contactVel = ball.velocity + Vector3.Cross(ball.angularVelocity, Vector3.down * RadiusOf(ball));
        contactVel.y = 0f;
        return contactVel.magnitude;
    }

    // Project settings leave Physics.autoSyncTransforms off, so a write to transform.position alone
    // moves the visible ball while the physics body keeps its old pose - and the next physics step
    // drags the ball back. Every respot/respawn has to move the body itself.
    private static void TeleportBall(Rigidbody ball, Vector3 position)
    {
        ball.velocity = Vector3.zero;
        ball.angularVelocity = Vector3.zero;
        ball.position = position;
        ball.transform.position = position;
    }

    private float RadiusOf(Rigidbody ball)
    {
        if (!ballRadius.TryGetValue(ball, out float r))
        {
            var sc = ball.GetComponent<SphereCollider>();
            Vector3 s = ball.transform.lossyScale;
            r = sc != null ? sc.radius * Mathf.Max(s.x, s.y, s.z) : 0f;
            ballRadius[ball] = r;
        }
        return r;
    }

    // ----- Confirm / Strike API (explicit, UI-friendly) -----
    public bool IsConfirmMode => confirmMode;
    public bool IsStrikeRequested => strikeRequested;
    // Placement replaces normal aiming input entirely, so it locks the cue the same way confirm does.
    public bool IsInputLocked => inputLocked || awaitingPlacement;

    // Called by the single on-screen button.
    // First press enters Confirm mode (locks input). Second press requests the strike.
    public void ConfirmButtonPressed()
    {
        if (frameOver)
        {
            Debug.Log("Frame is over - no further shots accepted.");
            return;
        }

        if (!nextplay)
        {
            Debug.Log("Cannot confirm while balls are moving.");
            return;
        }

        // Phase 3: binding nomination rule (5.1) - can't proceed to a shot on Colour
        // until a colour has actually been chosen, UNLESS reds are gone, in which case
        // the colour sequence is fixed and there's nothing to nominate.
        if (NeedsColourNomination)
        {
            Debug.Log("Cannot confirm: nominate a colour first (Colour state, reds still on table).");
            return;
        }

        // Ball in hand: no shot until the cue ball has been placed in the D.
        if (awaitingPlacement)
        {
            Debug.Log("Cannot confirm: place the cue ball in the D first.");
            return;
        }

        // NOTE: Confirm no longer fires the strike itself. It only locks aim so the
        // ShotPowerSlider becomes interactable - the actual strike happens when the
        // player releases the power slider (see ShotPowerSlider.OnPointerUp).
        if (!confirmMode)
        {
            confirmMode = true;
            inputLocked = true;
            Debug.Log("Confirm pressed: input locked. Use the power slider to set power and release to strike.");
        }
        else
        {
            Debug.Log("Already in confirm mode - use the power slider to strike, or press Clear to cancel.");
        }
    }

    // "Clear" button - cancels aiming/confirm WITHOUT striking, goes back to free aim.
    public void CancelConfirm()
    {
        confirmMode = false;
        inputLocked = false;
        strikeRequested = false;
    }

    // Called at the moment a shot is actually taken. Clearing the buffer here (rather than
    // after it's read) means PottedThisShot always reflects "what happened in the shot
    // currently in progress or just finished" - exactly what section 4 of the doc needs.
    public void RequestStrike()
    {
        if (frameOver || awaitingPlacement) return;

        strikeRequested = true;
        strikeSpin = spinOffset;
        spinOffset = Vector2.zero;
        PottedThisShot.Clear();
        firstBallContacted = null; // Phase 4: fresh shot, no contact recorded yet
    }
    public void ClearStrikeRequest() => strikeRequested = false;

    // NOTE: Cue.cs should NOT call this right after applying force anymore.
    // It's still here (public) in case other code needs to force-cancel confirm mode,
    // but the normal unlock now happens automatically via OnAllBallsStopped above.
    public void ClearConfirmMode()
    {
        confirmMode = false;
        inputLocked = false;
    }

    // Single source of truth for shot force - no second hidden multiplier in here anymore.
    public float GetStrikeForce() => baseStrikeForce;

    public void SetStrikeForce(float force)
    {
        if (force <= 0f) force = 0.1f;
        baseStrikeForce = force;
        Debug.Log($"Strike base force set to: {baseStrikeForce}");
    }

    // ----- Spin (SPIN_LOGIC.md) -----
    public Vector2 SpinOffset => spinOffset;
    public Vector2 StrikeSpin => strikeSpin;

    // Called by SpinSelectorUI while the player drags the hit-point dot. Locked once aim is
    // confirmed, and clamped inside 85% of the ball so the outer (miscue) ring is unreachable.
    public void SetSpinOffset(Vector2 offset)
    {
        if (confirmMode || awaitingPlacement) return;
        spinOffset = Vector2.ClampMagnitude(offset, MaxSpinRadius);
    }

    // ----- Phase 2: Potting -----
    // Called by PocketTrigger.cs when a ball's collider enters a pocket's trigger volume.
    // No rule/scoring logic here on purpose (that's Phase 3/4) - this is pure
    // pot -> deactivate/pool pipeline, as scoped in the doc.
    public void OnBallPotted(Rigidbody ball)
    {
        if (ball == null) return;

        // Already handled this ball this physics step (a fast ball can graze a trigger
        // more than once) - ignore duplicates.
        if (PottedThisShot.Contains(ball)) return;

        if (debugLogging) Debug.Log($"[GMDebug] Potted: {ball.gameObject.name}");

        if (ball == cueBall)
        {
            // Cue ball is never permanently deactivated - "ball in hand" respot.
            // Full foul handling for this comes in Phase 4; for now just get it back
            // on the table so testing/play can continue.
            ball.velocity = Vector3.zero;
            ball.angularVelocity = Vector3.zero;

            if (cueBallRespawnPoint != null)
                TeleportBall(ball, cueBallRespawnPoint.position);
            else
                Debug.LogWarning("GameManager: cueBallRespawnPoint not assigned - cue ball left where it was potted.", this);

            // Stays active - just repositioned.
        }
        else
        {
            ball.velocity = Vector3.zero;
            ball.angularVelocity = Vector3.zero;
            ball.gameObject.SetActive(false); // pool it, don't destroy it
        }

        PottedThisShot.Add(ball);
        OnBallPottedEvent?.Invoke(ball);
    }

    // Called by CueBallContactTracker.cs (on the cue ball) the instant the cue ball
    // physically hits another ball. Only the FIRST contact per shot is kept.
    public void ReportCueBallContact(Rigidbody other)
    {
        if (firstBallContacted != null) return; // already recorded this shot's first contact
        if (other == cueBall) return; // shouldn't happen, but guard anyway
        firstBallContacted = other;
        if (debugLogging)
        {
            var id = other.GetComponent<BallIdentity>();
            Debug.Log($"[GMDebug] First contact this shot: {(id != null ? id.Type.ToString() : other.gameObject.name)}");
        }
    }

    // ======================================================================
    // ----- Ball in hand: placement in the D (BALL_PLACEMENT_D.md) -----
    // ======================================================================

    public bool IsAwaitingPlacement => awaitingPlacement;
    public Vector3 DCenter => dCenter;
    public float DRadius => dRadius;
    public Vector3 DOpenDirection => dOpenDirection;
    public Vector3 LastValidPlacement => lastValidPlacement;

    // Brown sits on the centre of the baulk line and Green/Yellow on the D's two ends, so their spawn
    // spots give the D directly; the black spot says which side of the baulk line the D opens toward.
    private void ComputeDGeometry()
    {
        Vector3? green = null, brown = null, yellow = null, black = null;
        foreach (var ball in balls)
        {
            if (ball == null) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id == null) continue;
            if (id.Type == BallType.Green) green = id.SpawnPosition;
            else if (id.Type == BallType.Brown) brown = id.SpawnPosition;
            else if (id.Type == BallType.Yellow) yellow = id.SpawnPosition;
            else if (id.Type == BallType.Black) black = id.SpawnPosition;
        }
        if (!green.HasValue || !brown.HasValue || !yellow.HasValue || !black.HasValue)
        {
            Debug.LogError("GameManager: Green, Brown, Yellow and Black must all be in the Balls list to derive the D.", this);
            return;
        }

        Vector3 alongBaulk = yellow.Value - green.Value;
        alongBaulk.y = 0f;
        dCenter = brown.Value;
        dRadius = alongBaulk.magnitude * 0.5f;

        Vector3 perpendicular = new Vector3(-alongBaulk.z, 0f, alongBaulk.x).normalized;
        Vector3 towardBlack = black.Value - brown.Value;
        dOpenDirection = Vector3.Dot(perpendicular, towardBlack) > 0f ? -perpendicular : perpendicular;

        if (debugLogging) Debug.Log($"[GMDebug] D derived: centre={dCenter} radius={dRadius:F3} opens toward {dOpenDirection}");
    }

    // Inside the half-circle on the baulk side of the line, and clear of every other ball.
    public bool IsValidPlacement(Vector3 point, out string reason)
    {
        Vector3 fromCentre = point - dCenter;
        fromCentre.y = 0f;
        if (fromCentre.magnitude > dRadius) { reason = "outside the D"; return false; }
        if (Vector3.Dot(fromCentre, dOpenDirection) < 0f) { reason = "on the wrong side of the baulk line"; return false; }

        float minGap = 2f * RadiusOf(cueBall);
        foreach (var ball in balls)
        {
            if (ball == null || ball == cueBall || !ball.gameObject.activeInHierarchy) continue;
            Vector3 gap = ball.transform.position - point;
            gap.y = 0f;
            if (gap.magnitude < minGap) { reason = $"overlapping {ball.gameObject.name}"; return false; }
        }

        reason = null;
        return true;
    }

    // Frame start, and after every foul (house rule: ball in hand after ANY foul - see the note
    // in BALL_PLACEMENT_D.md §1). The ball stays put if it's already somewhere legal in the D.
    private void BeginPlacement()
    {
        if (frameOver || cueBall == null) return;

        Vector3 current = cueBall.transform.position;
        if (IsValidPlacement(current, out _))
        {
            lastValidPlacement = current;
        }
        else
        {
            lastValidPlacement = FindDefaultPlacement(current.y);
            TeleportBall(cueBall, lastValidPlacement);
        }

        awaitingPlacement = true;
        RefreshPlacementPanel();
        if (debugLogging) Debug.Log($"[GMDebug] Ball in hand for Player {currentPlayerIndex} - place the cue ball in the D.");
    }

    // Respawn point first (it already sits in the D), then spots fanning out across the D.
    // Brown occupies the baulk-line centre, so that spot is never a legal default.
    private Vector3 FindDefaultPlacement(float y)
    {
        if (cueBallRespawnPoint != null)
        {
            Vector3 p = cueBallRespawnPoint.position;
            p.y = y;
            if (IsValidPlacement(p, out _)) return p;
        }

        Vector3 alongBaulk = Vector3.Cross(Vector3.up, dOpenDirection);
        for (int ring = 1; ring <= 4; ring++)
        {
            float r = dRadius * ring / 5f;
            for (int step = 0; step <= 8; step++)
            {
                float angle = Mathf.PI * step / 8f;
                Vector3 p = dCenter + (alongBaulk * Mathf.Cos(angle) + dOpenDirection * Mathf.Sin(angle)) * r;
                p.y = y;
                if (IsValidPlacement(p, out _)) return p;
            }
        }
        Vector3 fallback = dCenter + dOpenDirection * (dRadius * 0.5f);
        fallback.y = y;
        return fallback;
    }

    // Called by CueBallPlacement when the player releases a drag. Only records the spot - the drag
    // controller moves the ball itself, back to LastValidPlacement when this returns false.
    public bool TryPlaceCueBall(Vector3 point)
    {
        if (!awaitingPlacement) return false;
        if (!IsValidPlacement(point, out string reason))
        {
            Debug.Log($"[GMDebug] Placement rejected at {point}: {reason}.");
            return false;
        }
        lastValidPlacement = point;
        return true;
    }

    // Tick button on the placement panel.
    public void ConfirmPlacement()
    {
        if (!awaitingPlacement) return;
        if (!IsValidPlacement(cueBall.transform.position, out string reason))
        {
            Debug.Log($"[GMDebug] Can't confirm placement - the cue ball is {reason}.");
            return;
        }
        EndPlacement();
        if (debugLogging) Debug.Log($"[GMDebug] Cue ball placed at {cueBall.transform.position} - aim and play.");
    }

    private void EndPlacement()
    {
        awaitingPlacement = false;
        RefreshPlacementPanel();
    }

    private void RefreshPlacementPanel()
    {
        if (placementPanel != null)
            placementPanel.SetActive(awaitingPlacement);
    }

    // ======================================================================
    // ----- Phase 3: Colour Logic, Selection & Scoring (doc section 5) -----
    // ======================================================================

    public enum TargetBallState { Red, Colour }

    [Header("Phase 3 - Colour Logic & Scoring")]
    [Tooltip("Starts on Red per 5.1 (assumes reds remain on table at frame start).")]
    [SerializeField] private TargetBallState targetState = TargetBallState.Red;

    // Set by the nomination buttons (OnColourNominated, via ColourNominateButton). Only
    // meaningful while targetState == Colour AND reds remain on table - once reds run out
    // the sequence below drives currentTargetColour automatically, no nomination needed.
    private BallType? currentTargetColour = null;

    [Header("Phase 5 - Colour Nomination Panel (manual - no ColourNominationUI script)")]
    [Tooltip("Drag your manually-built ColourSlectionPanel here. GameManager shows/hides it " +
             "based on NeedsColourNomination - nothing else needs to control it.")]
    [SerializeField] private GameObject colourNominationPanel;

    public TargetBallState CurrentTargetState => targetState;
    public BallType? CurrentTargetColour => currentTargetColour;

    // True exactly when the UI should be showing the colour-nomination picker and
    // blocking Confirm until the player taps one.
    public bool NeedsColourNomination =>
        targetState == TargetBallState.Colour && currentTargetColour == null && RedsRemainingOnTable() > 0;

    // 5.2 Points table.
    public static readonly Dictionary<BallType, int> BallValue = new()
    {
        { BallType.Red, 1 },
        { BallType.Yellow, 2 },
        { BallType.Green, 3 },
        { BallType.Brown, 4 },
        { BallType.Blue, 5 },
        { BallType.Pink, 6 },
        { BallType.Black, 7 },
    };

    // Fixed potting order once all reds are gone.
    private static readonly BallType[] ColourSequence =
    {
        BallType.Yellow, BallType.Green, BallType.Brown, BallType.Blue, BallType.Pink, BallType.Black
    };
    private int colourSequenceIndex = -1; // -1 = not yet in colour-sequence phase

    // ----- Scoring (minimal 2-player version - Phase 4 owns turn-switching-on-foul) -----
    [Header("Phase 3 - Scoring")]
    [SerializeField] private int[] playerScores = new int[2];
    [SerializeField] private int currentPlayerIndex = 0;

    public int GetScore(int playerIndex) => playerScores[playerIndex];
    public int CurrentPlayerIndex => currentPlayerIndex;

    // UI hooks (scoreboard, ball-on indicator - Phase 5 consumes these, doesn't produce them).
    public event Action<int, int> OnScoreChanged;          // (playerIndex, newScore)
    public event Action<TargetBallState, BallType?> OnTargetChanged; // (state, nominated/sequence colour)
    public event Action<int> OnTurnChanged;                // (newCurrentPlayerIndex)

    // Called by the nomination UI when the player taps a colour while on Colour state.
    public void OnColourNominated(BallType chosen)
    {
        if (targetState != TargetBallState.Colour)
        {
            Debug.LogWarning($"[GMDebug] Ignoring nomination of {chosen} - not currently in Colour state.");
            return;
        }
        if (RedsRemainingOnTable() == 0)
        {
            Debug.LogWarning("[GMDebug] Ignoring nomination - reds are gone, colour order is fixed now.");
            return;
        }
        if (chosen == BallType.Red || chosen == BallType.Cue)
        {
            Debug.LogWarning($"[GMDebug] {chosen} is not a nominatable colour.");
            return;
        }

        currentTargetColour = chosen;
        if (debugLogging) Debug.Log($"[GMDebug] Colour nominated: {chosen}");
        OnTargetChanged?.Invoke(targetState, currentTargetColour);
    }

    // How many reds are still live on the table (active in scene, not pooled).
    private int RedsRemainingOnTable()
    {
        int count = 0;
        foreach (var ball in balls)
        {
            if (ball == null || !ball.gameObject.activeInHierarchy) continue;
            var identity = ball.GetComponent<BallIdentity>();
            if (identity != null && identity.Type == BallType.Red) count++;
        }
        return count;
    }

    private void AwardPoints(int playerIndex, int points)
    {
        playerScores[playerIndex] += points;
        if (debugLogging) Debug.Log($"[GMDebug] Player {playerIndex} awarded {points} pts (total {playerScores[playerIndex]})");
        OnScoreChanged?.Invoke(playerIndex, playerScores[playerIndex]);
    }

    // Runs once per completed shot (subscribed to OnAllBallsStopped in Awake, AFTER EvaluateFoul).
    // NOTE: scoring itself now lives entirely in EvaluateFoul (Phase 4) - this method only
    // applies the PHYSICAL consequences of what was potted (respawn/permanent removal,
    // targetState transitions), same as it always did. Awarding points here as well would
    // double-count on top of EvaluateFoul's NoFoul()/Foul() results.
    private void EvaluateShotResult()
    {
        if (PottedThisShot.Count == 0) return; // nothing potted this shot - a miss, Phase 4's job

        foreach (var potted in PottedThisShot)
        {
            if (potted == cueBall) continue; // cue ball never scores - Phase 4 fouls it instead

            var identity = potted.GetComponent<BallIdentity>();
            if (identity == null)
            {
                Debug.LogWarning($"[GMDebug] {potted.gameObject.name} was potted but has no BallIdentity - " +
                                  "add one so Phase 3 scoring can see its type.", potted);
                continue;
            }

            if (identity.Type == BallType.Red)
            {
                // Only a LEGAL red pot earns the colour. A red that drops as part of a foul shot
                // leaves the incoming player on Red, per the real rule - and crucially, flipping
                // here on a foul used to strand the frame on Colour for good, because nothing but a
                // legal colour pot flips it back. That made both players hunt colours while fifteen
                // reds sat untouched.
                if (targetState == TargetBallState.Red && !lastShotWasFoul)
                {
                    targetState = TargetBallState.Colour;
                    currentTargetColour = null; // must be nominated before next shot (or auto-set if reds now gone)
                    OnTargetChanged?.Invoke(targetState, currentTargetColour);
                }
            }
            else
            {
                ResolveColourPot(identity, potted);
            }
        }
    }

    // 5.3 Respawn logic (physical placement only - scoring now lives in EvaluateFoul).
    private void ResolveColourPot(BallIdentity identity, Rigidbody colourBall)
    {
        if (RedsRemainingOnTable() > 0)
        {
            // [Assumed, polish item] Spot-conflict rule (real snooker: nearest available spot
            // up the table if occupied) is not handled yet - straight respot to SpawnPosition
            // for now, per the doc's note that this edge case is rare and can be revisited later.
            TeleportBall(colourBall, identity.SpawnPosition);
            colourBall.gameObject.SetActive(true);

            targetState = TargetBallState.Red;
            currentTargetColour = null;

            if (debugLogging) Debug.Log($"[GMDebug] {identity.Type} respawned to spot - back on Red.");
        }
        else
        {
            // Reds are gone - this colour stays off permanently, sequence advances.
            // (Ball is already deactivated/pooled by OnBallPotted's Phase 2 pipeline.)
            AdvanceColourSequence();
        }

        OnTargetChanged?.Invoke(targetState, currentTargetColour);
    }

    private void AdvanceColourSequence()
    {
        colourSequenceIndex++;
        targetState = TargetBallState.Colour;

        if (colourSequenceIndex < ColourSequence.Length)
        {
            currentTargetColour = ColourSequence[colourSequenceIndex];
            if (debugLogging) Debug.Log($"[GMDebug] Colour sequence advanced - next up: {currentTargetColour}");
        }
        else
        {
            currentTargetColour = null;
            EndFrame();
        }
    }

    // The black has gone down off the end of the colour sequence - the frame is over.
    // Highest score wins; equal scores are reported as a tie (the real respotted-black
    // tie-break procedure is not modelled).
    private void EndFrame()
    {
        if (frameOver) return;
        frameOver = true;

        confirmMode = false;
        inputLocked = true;
        strikeRequested = false;
        if (awaitingPlacement) EndPlacement(); // a foul on the final black still ends the frame

        int winner = -1;
        if (playerScores[0] > playerScores[1]) winner = 0;
        else if (playerScores[1] > playerScores[0]) winner = 1;

        Debug.Log(winner >= 0
            ? $"[GMDebug] FRAME OVER - Player {winner} wins {playerScores[0]}-{playerScores[1]}."
            : $"[GMDebug] FRAME OVER - tie at {playerScores[0]}-{playerScores[1]}.");

        OnFrameEnded?.Invoke(winner);
    }

    // ======================================================================
    // ----- Phase 4: Foul Logic (doc section 6) -----
    // ======================================================================
    //
    // Single EvaluateFoul() function driving everything off the decision tables in 6.2/6.3,
    // exactly as the doc recommends over scattered if/else. This is the ONLY place that
    // awards points and passes turns - Phase 3's EvaluateShotResult (above) only handles
    // the physical respawn/removal side now.
    //
    // Runs BEFORE EvaluateShotResult (see subscription order in Awake) specifically so it
    // reads targetState/currentTargetColour as they were going INTO this shot.

    private int OpponentIndex => (currentPlayerIndex + 1) % playerScores.Length;

    private void PassTurn()
    {
        currentPlayerIndex = OpponentIndex;

        // A colour is only "on" for the player who just potted a red. The moment their turn ends -
        // missed the colour, or fouled - the incoming player is back on Red while reds remain.
        // Without this the Colour state carried across the turn change, so BOTH players kept hunting
        // that one colour with a full pack of reds still on the table.
        // Reds gone is the exception: there the fixed Yellow->Black sequence must persist.
        if (targetState == TargetBallState.Colour && RedsRemainingOnTable() > 0)
        {
            targetState = TargetBallState.Red;
            currentTargetColour = null;
            OnTargetChanged?.Invoke(targetState, currentTargetColour);
        }

        if (debugLogging) Debug.Log($"[GMDebug] Turn passed - now Player {currentPlayerIndex}");
        OnTurnChanged?.Invoke(currentPlayerIndex);
    }

    private BallType? GetBallType(Rigidbody rb)
    {
        if (rb == null) return null;
        var id = rb.GetComponent<BallIdentity>();
        return id != null ? id.Type : (BallType?)null;
    }

    // Any ball OTHER than a red (and other than the cue ball) potted this shot.
    private bool AnyNonRedPottedThisShot()
    {
        foreach (var b in PottedThisShot)
        {
            if (b == cueBall) continue;
            var type = GetBallType(b);
            if (type.HasValue && type.Value != BallType.Red) return true;
        }
        return false;
    }

    // Highest BallValue among illegally-potted (non-red, non-cue) balls this shot.
    // Cue ball is deliberately excluded here - its foul is always floored to 4 by the
    // Math.Max(4, ...) callers regardless of this value.
    private int HighestValuePottedIllegally()
    {
        int highest = 0;
        foreach (var b in PottedThisShot)
        {
            if (b == cueBall) continue;
            var type = GetBallType(b);
            if (!type.HasValue || type.Value == BallType.Red) continue;
            if (BallValue.TryGetValue(type.Value, out int v)) highest = Mathf.Max(highest, v);
        }
        return highest;
    }

    // 6.1-6.4: single end-of-shot foul decision, run once per completed shot.
    private void EvaluateFoul()
    {
        lastShotWasFoul = false;
        bool cueBallPotted = cueBall != null && PottedThisShot.Contains(cueBall);
        BallType? firstType = GetBallType(firstBallContacted);

        bool legal;
        int points;

        if (targetState == TargetBallState.Red)
        {
            // 6.2 - Player On Red
            // !firstType.HasValue means the recorded contact wasn't an identifiable ball, which
            // is scored the same as hitting nothing - and keeps firstType.Value below safe.
            if (cueBallPotted || firstBallContacted == null || !firstType.HasValue)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[BallType.Red]); // 6.5: potting cue ball / hitting nothing
            }
            else if (firstType != BallType.Red)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[firstType.Value]);
            }
            else if (AnyNonRedPottedThisShot())
            {
                legal = false;
                points = Mathf.Max(4, HighestValuePottedIllegally());
            }
            else
            {
                legal = true;
                int redsPotted = 0;
                foreach (var b in PottedThisShot)
                {
                    var t = GetBallType(b);
                    if (t.HasValue && t.Value == BallType.Red) redsPotted++;
                }
                points = redsPotted * BallValue[BallType.Red];
            }
        }
        else
        {

            if (!currentTargetColour.HasValue)
            {

                Debug.LogWarning("[GMDebug] EvaluateFoul: on Colour with no currentTargetColour - skipping foul check.");
                return;
            }

            BallType target = currentTargetColour.Value;

            if (cueBallPotted || firstBallContacted == null || !firstType.HasValue)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[target]);
            }
            else if (firstType != target)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[firstType.Value]);
            }
            else if (PottedThisShot.Count == 1 && GetBallType(PottedThisShot[0]) == target)
            {
                legal = true;
                points = BallValue[target];
            }
            else if (PottedThisShot.Count == 0)
            {
                // Hit the colour that was on and potted nothing: a legal miss, same as on Red - no
                // points, the turn simply passes. Scoring it as a foul penalised every missed colour
                // and every safety played on a colour.
                legal = true;
                points = 0;
            }
            else
            {

                legal = false;
                points = Mathf.Max(4, BallValue[target]);
            }
        }

        // 6.4 - apply the result.
        if (legal)
        {
            if (points > 0)
            {
                AwardPoints(currentPlayerIndex, points);

            }
            else
            {

                PassTurn();
            }
        }
        else
        {
            lastShotWasFoul = true;
            AwardPoints(OpponentIndex, points);
            PassTurn();

            if (debugLogging) Debug.Log($"[GMDebug] FOUL: {points} pts to Player {OpponentIndex}.");

            // Ball in hand is driven by the cue ball being off the table, NOT by "a foul happened"
            // (BALL_PLACEMENT_D.md section 1, corrected rule). On every other foul - wrong ball hit
            // first, wrong colour potted - the cue ball is still on the cloth, so the incoming player
            // plays it from where it lies, exactly as in tournament snooker.
            if (cueBallPotted) BeginPlacement();
        }
    }
}
