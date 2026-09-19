using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// One line of commentary per shot, picked from Player_* or AI_* pools depending on who shot and
// whether it was potted. Kept as a plain enum here (no new .cs file) since it's only ever used to
// pick which of the three POT pools to play - see ClassifyShotType/PrepareCommentaryForShot below.
public enum CommentaryShotType { EasyPot, LongPot, CutPot }

// Which specific foul happened on the last shot, for the foul commentary clips. Set inline inside
// EvaluateFoul, right alongside the checks that already decide legal vs foul - not a second check.
public enum FoulReason { None, CueBallFoul, WrongBallFoul, NoBallHitFoul }

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
    [Tooltip("Optional: if left empty, found via FindObjectOfType at Start. Used only for the free-ball " +
             "snooker line-of-sight check (Cue.IsPathClear) - see EvaluateFreeBallEligibility.")]
    [SerializeField] private Cue cue;

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

    [Header("Foul Play/Play-Again Decision (FOUL_PLAY_AGAIN_RULE.md)")]
    [Tooltip("Shown while IsAwaitingFoulDecision is true: Play / Make opponent play again buttons, " +
             "wired to ChooseFoulPlay/ChooseFoulPlayAgain.")]
    [SerializeField] private GameObject foulDecisionPanel;

    [Header("TEMP DEBUG - delete after fixing")]
    [SerializeField] private bool debugLogging = true;
    private float debugLogTimer2 = 0f;

    [Header("Commentary System")]
    [Tooltip("Cut angle (degrees, between cue-to-object and object-to-pocket) above which a pot " +
             "counts as a Difficult Cut for commentary purposes.")]
    public float commentaryCutAngleThreshold = 35f;
    [Tooltip("Total shot distance (cue-to-object plus object-to-pocket, world units) above which a " +
             "pot counts as a Long Pot. Default is roughly 60% of this table's ~17-unit long side.")]
    public float commentaryLongPotDistance = 10f;
    [Tooltip("Seconds to wait after the balls settle before the commentary line plays.")]
    public float commentaryDelay = 0.5f;
    [Tooltip("Safety ceiling: if the commentary queue is somehow still playing after this many " +
             "seconds, it is force-stopped so the next shot can never be blocked forever.")]
    public float commentaryTimeoutSeconds = 15f;

    [Header("Commentary Clips - Player")]
    public AudioClip[] Player_EasyPot;
    public AudioClip[] Player_LongPot;
    public AudioClip[] Player_CutPot;
    public AudioClip[] Player_Miss;

    [Header("Commentary Clips - AI")]
    public AudioClip[] AI_EasyPot;
    public AudioClip[] AI_LongPot;
    public AudioClip[] AI_CutPot;
    public AudioClip[] AI_Miss;

    [Header("Commentary Clips - Ball Names (shared Player/AI)")]
    [Tooltip("Plays for whichever ball the cue ball hits first this shot. Turn off below to skip it.")]
    public bool playBallNameClip = true;
    public AudioClip Red;
    public AudioClip Yellow;
    public AudioClip Green;
    public AudioClip Brown;
    public AudioClip Blue;
    public AudioClip Pink;
    public AudioClip Black;

    [Header("Commentary Clips - Fouls (shared Player/AI)")]
    [Tooltip("Cue ball potted.")]
    public AudioClip CueBallFoul;
    [Tooltip("Wrong ball hit first (e.g. Red was required but Blue was hit).")]
    public AudioClip WrongBallFoul;
    [Tooltip("Cue ball touched no ball at all.")]
    public AudioClip NoBallHitFoul;

    [Header("Commentary Clips - Extra")]
    [Tooltip("Plays after a red ball is legally potted (next shot must be a colour).")]
    public AudioClip AfterRedBall;

    [Tooltip("Optional: AudioSource commentary lines play through. If left empty, one is added " +
             "automatically on this GameObject.")]
    [SerializeField] private AudioSource commentaryAudioSource;

    // Computed by PrepareCommentaryForShot at the moment the shot is struck (before anything moves)
    // and consumed once by PlayShotCommentary once the shot has fully resolved.
    private Rigidbody pendingCommentaryObjectBall;
    private CommentaryShotType pendingCommentaryShotType;
    private bool pendingCommentaryWasAiTurn;
    private bool pendingCommentaryIsBreakOff;
    private int shotsTakenThisFrame = 0;
    // Set by PlayCommentaryForFinishedShot (OnAllBallsStopped) once the shot's foul/pot/miss result is
    // final - the ball-name half of PlayShotCommentary, already running since the shot was struck,
    // waits on this before it can work out and play the result clip.
    private bool shotHasResolved = false;
    private readonly List<Transform> pocketTransforms = new List<Transform>();
    private readonly Dictionary<AudioClip[], int> lastCommentaryIndex = new Dictionary<AudioClip[], int>();
    private Coroutine commentaryCoroutine;
    private FoulReason lastFoulReason = FoulReason.None;
    public FoulReason LastFoulReason => lastFoulReason;

    // True for the whole span of PlayShotCommentary - from the moment the shot is struck (its live
    // ball-name clip) through to its last result clip finishing (or the safety timeout firing). Read
    // by IsAimFrozen/ConfirmButtonPressed/RequestStrike/SnookerAI so the next shot waits for it,
    // instead of cutting the commentary off.
    private bool isCommentaryPlaying = false;
    public bool IsCommentaryPlaying => isCommentaryPlaying;

    // state
    private bool nextplay = false;
    private bool strikeRequested = false;
    private bool confirmMode = false;
    private bool inputLocked = false;
    private bool frameOver = false;
    private bool awaitingPlacement = false;

    // FOUL_PLAY_AGAIN_RULE.md section 2: after any foul, the non-offending player (this index)
    // decides whether to play on or send the fouling player back in, before any placement-in-D
    // or normal-aim flow starts. foulDecisionCueBallPotted remembers whether ball-in-hand should
    // follow once the decision resolves, since it applies to whichever player ends up actually
    // playing next, not necessarily foulDecisionForPlayerIndex.
    private bool awaitingFoulDecision = false;
    private int foulDecisionForPlayerIndex = -1;
    private bool foulDecisionCueBallPotted = false;

    // Which player index (if any) is AI-controlled, and whether the AI is mid-way through taking its
    // own shot right now. Registered by SnookerAI.Start(); used only to stop a human's own UI input
    // (Confirm button, colour nomination, the power slider) from being accepted during the AI's turn -
    // none of those are gated on whose turn it is, so a stray click while the AI is "thinking" could set
    // confirmMode/currentTargetColour out from under it. The AI's own calls always pass aiIsActing=true
    // around themselves, so this never blocks the AI acting on its own turn.
    private int aiPlayerIndex = -1;
    private bool aiIsActing = false;
    public void RegisterAiPlayer(int playerIndex) => aiPlayerIndex = playerIndex;
    public void SetAiActing(bool acting) => aiIsActing = acting;
    public bool IsAiTurn => aiPlayerIndex >= 0 && currentPlayerIndex == aiPlayerIndex;
    private bool BlockedAsHumanInputDuringAiTurn => IsAiTurn && !aiIsActing;

    // Set by EvaluateFoul, read by EvaluateShotResult, which runs straight after it. A red potted on
    // a foul shot must NOT advance Red -> Colour: after a foul the incoming player is still on Red
    // while reds remain. Without this the state flipped anyway, and since only a legal colour pot
    // flips it back, the frame got stuck on Colour with reds still on the table.
    private bool lastShotWasFoul = false;
    // Exposed so pot celebration effects (PocketTrigger) can hold off spawning until the whole shot
    // resolves and this is known - a ball dropping mid-shot doesn't yet know if the shot is a foul.
    public bool LastShotWasFoul => lastShotWasFoul;

    // ----- Free Ball (FREE_BALL.md) -----
    // Set true by EvaluateFoul whenever a foul just occurred; consumed (checked and cleared) once the
    // resulting cue ball position is actually final - see the callers of EvaluateFreeBallEligibility.
    // Not evaluated immediately inside EvaluateFoul because a potted cue ball means ball-in-hand: the
    // real resting position (and therefore whether the incoming player is really snookered) isn't
    // known until they've placed it.
    private bool pendingFreeBallCheck = false;
    // Awarded for exactly one upcoming shot once EvaluateFreeBallEligibility confirms every current
    // ball-on is unreachable as a direct result of the foul just evaluated.
    private bool freeBallAvailable = false;
    // Reds-on-table status captured at the moment the free ball was awarded (not re-read later, since
    // the free-ball shot itself can change that count before scoring/consequences are resolved).
    private bool freeBallRedsRemained = false;
    // The substitute ball-on the player has chosen for the pending free-ball shot, if any - see
    // NominateFreeBall. Null means they haven't nominated (or don't intend to use the free ball).
    private BallType? freeBallNomination = null;

    // Snapshots of the two fields above taken by EvaluateFoul at the start of evaluating a shot, so
    // EvaluateShotResult (which runs immediately after) can apply the free-ball substitution rules to
    // whatever was potted THIS shot without racing the reset below, which clears the "next shot"
    // fields unconditionally once this shot's legality is known.
    private BallType? lastShotFreeBallNomination = null;
    private bool lastShotFreeBallRedsRemained = false;

    public bool IsFreeBallAvailable => freeBallAvailable;
    public BallType? FreeBallNomination => freeBallNomination;

    // Called by whatever lets the player pick a ball-on for a free-ball shot (BallAimClickTarget's
    // click-to-aim doubles as nomination here, or the AI via SnookerAI's normal nomination pipeline -
    // see NominationFor). Unlike OnColourNominated, any ball except the cue is acceptable - the doc
    // allows nominating "any ball on the table", not just colours.
    public void NominateFreeBall(BallType chosen)
    {
        if (BlockedAsHumanInputDuringAiTurn) return;
        if (confirmMode || awaitingPlacement || awaitingFoulDecision) return;
        if (!freeBallAvailable) return;
        if (chosen == BallType.Cue) return;

        freeBallNomination = chosen;
        if (debugLogging) Debug.Log($"[GMDebug] Free ball nominated: {chosen}");
    }

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

        // Commentary: runs last, after EvaluateFoul/EvaluateShotResult above, so LastShotWasFoul and
        // PottedThisShot are both fully settled by the time it decides POT vs MISS.
        OnAllBallsStopped += PlayCommentaryForFinishedShot;

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

        // Needed for the free-ball snooker check (Cue.IsPathClear) - see EvaluateFreeBallEligibility.
        if (cue == null) cue = FindObjectOfType<Cue>();
        if (cue == null)
            Debug.LogWarning("GameManager: No Cue found in scene - free-ball snooker detection will be skipped.", this);

        // Commentary: same discovery pattern SnookerAI already uses for its own pocket geometry.
        pocketTransforms.Clear();
        foreach (var pocket in FindObjectsOfType<PocketTrigger>())
            pocketTransforms.Add(pocket.transform);

        Debug.Log($"GameManager initialized: baseStrikeForce={baseStrikeForce} => GetStrikeForce()={GetStrikeForce()}");

        // Set correct initial visibility (starts hidden, since targetState starts on Red).
        RefreshColourNominationPanel();

        // Frame start: the first player places the cue ball in the D before anyone strikes.
        ComputeDGeometry();
        BeginPlacement();
    }

    // Shows the manually-built panel exactly when NeedsColourNomination is true, hides it
    // otherwise. Called from Awake's OnTargetChanged subscription and once at Start.
    // The picker panel is no longer how a colour gets nominated - the player clicks the actual
    // ball on the table instead (ColourBallClickTarget), reflected on the Canvas by
    // SelectedColourIndicator. Kept as a permanently-hidden method rather than removing the field
    // and every reference to it.
    private void RefreshColourNominationPanel()
    {
        if (colourNominationPanel != null)
            colourNominationPanel.SetActive(false);
    }

    public void Cam1() => cameraSwitching?.SwitchToTopDownCamera();
    public void Cam2() => cameraSwitching?.SwitchToThirdPersonCamera();
    public void Cam3() => cameraSwitching?.SwitchToFirstPersonCamera();

    // Wired to the single camera-toggle button: alternates top-down/third-person on every click.
    public void ToggleCamera() => cameraSwitching?.ToggleTopDownThirdPerson();

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

            // Force-zeroing a ball's velocity here (below a separate, coarser "snapToZeroThreshold")
            // used to fire before BallRollingFriction's own hard-stop (stopSpeedThreshold, much finer
            // and slip-aware) got a chance to run - cutting the last, most natural part of a real
            // roll-to-a-stop short. BallRollingFriction already zeroes velocity/angularVelocity itself
            // once a ball is genuinely at rest, every FixedUpdate, so that's removed here - this loop
            // only needs to WATCH speed/slip for the anyMoving check above, not force-stop anything.
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
    // Blocks Confirm/Strike/spin/ball-click-targeting - anything that could commit to or start a shot.
    public bool IsInputLocked => inputLocked || awaitingPlacement || awaitingFoulDecision;
    // Narrower than IsInputLocked: only the cases where the cue itself should stop moving entirely
    // (post-Confirm, ball-in-hand, and commentary still playing for the last shot). A pending foul
    // decision does NOT belong here - the player still needs to freely look around the table (via the
    // aim-follow camera) to judge the position before choosing Play or Play Again; only actually
    // taking a shot is blocked during that choice, which ConfirmButtonPressed/RequestStrike already
    // refuse on their own regardless of this.
    public bool IsAimFrozen => inputLocked || awaitingPlacement || isCommentaryPlaying;

    // Called by the single on-screen button.
    // First press enters Confirm mode (locks input). Second press requests the strike.
    public void ConfirmButtonPressed()
    {
        if (frameOver)
        {
            Debug.Log("Frame is over - no further shots accepted.");
            return;
        }

        if (BlockedAsHumanInputDuringAiTurn)
        {
            Debug.Log("[GMDebug] Ignoring Confirm - it's the AI's turn.");
            return;
        }

        if (!nextplay)
        {
            Debug.Log("Cannot confirm while balls are moving.");
            return;
        }

        // The commentary for the shot that just finished is still playing - hold the next shot back
        // until it's done, instead of letting Confirm through and then silently rejecting the strike.
        if (isCommentaryPlaying)
        {
            Debug.Log("Cannot confirm: commentary for the last shot is still playing.");
            return;
        }

        // FOUL_PLAY_AGAIN_RULE.md section 2: the non-offending player must decide play vs
        // play-again before any normal aim/Confirm flow starts.
        if (awaitingFoulDecision)
        {
            Debug.Log("Cannot confirm: a foul decision (play or play-again) is pending.");
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
        if (BlockedAsHumanInputDuringAiTurn) return;
        confirmMode = false;
        inputLocked = false;
        strikeRequested = false;
    }

    // Called at the moment a shot is actually taken. Clearing the buffer here (rather than
    // after it's read) means PottedThisShot always reflects "what happened in the shot
    // currently in progress or just finished" - exactly what section 4 of the doc needs.
    public void RequestStrike()
    {
        if (frameOver || awaitingPlacement || awaitingFoulDecision) return;
        if (BlockedAsHumanInputDuringAiTurn) return;
        // Belt-and-braces: ConfirmButtonPressed already refuses to enter confirm mode while
        // commentary is playing, so this should be unreachable in normal play - kept here in case
        // anything else ever calls RequestStrike directly.
        if (isCommentaryPlaying) return;

        // Requesting a strike only makes sense once Confirm has actually locked the shot in - Cue.cs
        // only ever fires off BOTH confirmMode and strikeRequested being true together. Setting this
        // unconditionally used to leave strikeRequested permanently true whenever ConfirmButtonPressed
        // had silently no-op'd for any reason (mode not entered, wrong turn, still awaiting nomination)
        // - nothing but a real strike ever clears it, so SnookerAI.MyTurnToAct() (which requires
        // !IsStrikeRequested) was then dead forever. Refusing here instead of blindly setting the flag
        // means a rejected request is simply a no-op the caller can safely retry, not a permanent stall.
        if (!confirmMode)
        {
            Debug.LogWarning("[GMDebug] RequestStrike ignored - not in confirm mode yet.");
            return;
        }

        strikeRequested = true;
        strikeSpin = spinOffset;
        spinOffset = Vector2.zero;
        PottedThisShot.Clear();
        firstBallContacted = null; // Phase 4: fresh shot, no contact recorded yet
        PrepareCommentaryForShot(); // Commentary: classify the shot now, before anything moves
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
        if (confirmMode || awaitingPlacement || awaitingFoulDecision) return;
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
        TryResolvePendingFreeBallCheck();
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

    // Which part of the frame we're in, once reds start being potted. Reds = normal reds-phase play
    // (pot a red, nominate and pot any colour, respot, repeat). ColourAfterLastRed = the one shot
    // right after the last red goes down legally - same as a normal red's colour, freely nominated
    // and respotted, just with no reds left to go back to. ColoursInOrder = the fixed Yellow-to-Black
    // sequence, once that one shot is over. THE single source of truth for which phase we're in -
    // every other part of the game (nomination, AdvanceColourSequence, the AI) reads it via
    // NeedsColourNomination/CurrentTargetColour rather than guessing from the reds count.
    private enum ColourPhase { Reds, ColourAfterLastRed, ColoursInOrder }
    private ColourPhase colourPhase = ColourPhase.Reds;

    [Header("Phase 3 - Colour Logic & Scoring")]
    [Tooltip("Starts on Red per 5.1 (assumes reds remain on table at frame start).")]
    [SerializeField] private TargetBallState targetState = TargetBallState.Red;

    // Set by the nomination buttons (OnColourNominated, via ColourNominateButton). Only meaningful
    // while targetState == Colour and we're not yet locked into the fixed order (Reds phase, or the
    // one free colour right after the last red) - once the fixed order starts, AdvanceColourSequence
    // drives currentTargetColour automatically, no nomination needed.
    private BallType? currentTargetColour = null;

    [Header("Phase 5 - Colour Nomination Panel (manual - no ColourNominationUI script)")]
    [Tooltip("Drag your manually-built ColourSlectionPanel here. GameManager shows/hides it " +
             "based on NeedsColourNomination - nothing else needs to control it.")]
    [SerializeField] private GameObject colourNominationPanel;

    public TargetBallState CurrentTargetState => targetState;
    public BallType? CurrentTargetColour => currentTargetColour;

    // True exactly when the UI should be showing the colour-nomination picker and blocking Confirm
    // until the player taps one - reds phase, or the one free colour right after the last red. Once
    // the fixed order has started there's nothing to nominate any more.
    public bool NeedsColourNomination =>
        targetState == TargetBallState.Colour && currentTargetColour == null && colourPhase != ColourPhase.ColoursInOrder;

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

    // True if a ball of this colour is still active on the table right now. Used by
    // AdvanceColourSequence to skip past a colour that's already missing (illegally potted earlier
    // and never respawned) instead of leaving the game asking for a ball that no longer exists.
    private bool IsColourOnTable(BallType type)
    {
        foreach (var ball in balls)
        {
            if (ball == null || !ball.gameObject.activeInHierarchy) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id != null && id.Type == type) return true;
        }
        return false;
    }

    // ----- Scoring (minimal 2-player version - Phase 4 owns turn-switching-on-foul) -----
    [Header("Phase 3 - Scoring")]
    [SerializeField] private int[] playerScores = new int[2];
    [SerializeField] private int currentPlayerIndex = 0;

    // ----- Normal scene only: hard cap on how many turns the (human) player gets in the whole
    // frame. Leave enforcePlayerTurnLimit false everywhere else - Pro is untouched.
    [Header("Legendary Mode - Player Turn Limit (Normal scene only)")]
    [Tooltip("Turn the player-turn-limit rule on. Leave false in every scene except Normal.")]
    [SerializeField] private bool enforcePlayerTurnLimit = false;
    [Tooltip("Player loses automatically (AI declared winner) after this many of their own turns, " +
             "win or not. Tweak freely - 4 or 5 per the design.")]
    [SerializeField] private int maxPlayerTurns = 5;
    private int playerTurnsTaken = 0;

    public int GetScore(int playerIndex) => playerScores[playerIndex];
    public int CurrentPlayerIndex => currentPlayerIndex;

    // UI hooks (scoreboard, ball-on indicator - Phase 5 consumes these, doesn't produce them).
    public event Action<int, int> OnScoreChanged;          // (playerIndex, newScore)
    public event Action<TargetBallState, BallType?> OnTargetChanged; // (state, nominated/sequence colour)
    public event Action<int> OnTurnChanged;                // (newCurrentPlayerIndex)

    // Called by the nomination UI when the player taps a colour while on Colour state.
    public void OnColourNominated(BallType chosen)
    {
        if (BlockedAsHumanInputDuringAiTurn)
        {
            Debug.LogWarning($"[GMDebug] Ignoring nomination of {chosen} - it's the AI's turn.");
            return;
        }
        if (targetState != TargetBallState.Colour)
        {
            Debug.LogWarning($"[GMDebug] Ignoring nomination of {chosen} - not currently in Colour state.");
            return;
        }
        if (colourPhase == ColourPhase.ColoursInOrder)
        {
            Debug.LogWarning("[GMDebug] Ignoring nomination - colour order is fixed now.");
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
        if (PottedThisShot.Count == 0)
        {
            // A total miss during the one free colour right after the last red still ends that shot -
            // pot, miss or foul all move on to the fixed order the same way (a legal pot does this
            // itself, further down, via ResolveColourPot - this only catches the "nothing potted" case,
            // which would otherwise be missed entirely by the early return below).
            if (colourPhase == ColourPhase.ColourAfterLastRed)
            {
                colourPhase = ColourPhase.ColoursInOrder;
                AdvanceColourSequence();
                OnTargetChanged?.Invoke(targetState, currentTargetColour);
            }
            return; // nothing potted this shot - a miss, Phase 4's job
        }

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

            // FREE_BALL.md: a LEGALLY potted free-ball nomination is treated as whatever it stood in
            // for - a red while reds remained (flip to Colour, nomination needed next, exactly like a
            // real red pot), or handed to ResolveColourPot otherwise, which itself now works out
            // whether that means a respot (still mid-order) or a permanent removal (fixed order) from
            // colourPhase. Only the ball actually nominated gets this - anything else potted the same
            // shot (or a fouled free-ball attempt) resolves through the normal branches below untouched.
            bool isFreeBallSubstitute = !lastShotWasFoul && lastShotFreeBallNomination.HasValue
                                         && identity.Type == lastShotFreeBallNomination.Value;
            if (isFreeBallSubstitute)
            {
                if (lastShotFreeBallRedsRemained)
                {
                    targetState = TargetBallState.Colour;
                    currentTargetColour = null;
                    OnTargetChanged?.Invoke(targetState, currentTargetColour);
                }
                else
                {
                    ResolveColourPot(identity, potted);
                }
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

        // Whatever removed the last red - once none are left there is nothing further to be "on Red"
        // for. A LEGAL last red earns the same player one more free colour of their choice, exactly
        // like any other red (the Red branch above already set targetState/currentTargetColour for
        // that). A last red potted as part of a FOUL skips that free colour entirely - straight to
        // the fixed order at Yellow, per the real rule that a foul gets no reward.
        if (RedsRemainingOnTable() == 0 && colourPhase == ColourPhase.Reds)
        {
            if (lastShotWasFoul)
            {
                colourPhase = ColourPhase.ColoursInOrder;
                AdvanceColourSequence();
            }
            else
            {
                colourPhase = ColourPhase.ColourAfterLastRed;
            }
            OnTargetChanged?.Invoke(targetState, currentTargetColour);
        }
    }

    // 5.3 Respawn logic (physical placement only - scoring now lives in EvaluateFoul). Respots
    // whenever we haven't locked into the fixed order yet - that covers the normal reds phase AND the
    // one free colour right after the last red (including the same shot that pots the last red
    // itself, since colourPhase hasn't flipped to ColourAfterLastRed yet at that exact point - it's
    // still "Reds" there, which also respots). Only once colourPhase is ColoursInOrder does a potted
    // colour stay down for good.
    private void ResolveColourPot(BallIdentity identity, Rigidbody colourBall)
    {
        if (colourPhase != ColourPhase.ColoursInOrder)
        {
            // [Assumed, polish item] Spot-conflict rule (real snooker: nearest available spot
            // up the table if occupied) is not handled yet - straight respot to SpawnPosition
            // for now, per the doc's note that this edge case is rare and can be revisited later.
            TeleportBall(colourBall, identity.SpawnPosition);
            colourBall.gameObject.SetActive(true);

            if (colourPhase == ColourPhase.ColourAfterLastRed)
            {
                // That free colour is dealt with now - pot, foul, whichever ball it actually was -
                // the fixed order starts at Yellow either way. Who plays next (same player on a legal
                // pot, opponent on a foul/miss) is already decided separately by EvaluateFoul/PassTurn.
                colourPhase = ColourPhase.ColoursInOrder;
                AdvanceColourSequence();
            }
            else
            {
                targetState = TargetBallState.Red;
                currentTargetColour = null;
                if (debugLogging) Debug.Log($"[GMDebug] {identity.Type} respawned to spot - back on Red.");
            }
        }
        else
        {
            // Fixed order: this colour stays off permanently, but the ball on only actually moves on
            // if this was a LEGAL pot of the ball that was actually required (see AdvanceColourSequence's
            // own identity check too - both guards are needed, since a shot can foul on wrong-first-contact
            // even while the correct colour still ends up potted). Any other outcome (a foul, or some
            // other colour dropping) leaves the ball on exactly where it was - the potted ball itself is
            // already deactivated/pooled by OnBallPotted.
            if (!lastShotWasFoul) AdvanceColourSequence(identity.Type);
        }

        OnTargetChanged?.Invoke(targetState, currentTargetColour);
    }

    // THE single place that decides which colour is required next, once all reds are gone - every
    // other part of the game (AI target selection, the foul check, the UI) reads CurrentTargetColour,
    // and this is the only place allowed to change it once the fixed sequence has started. Call with
    // no argument to start the sequence fresh (the instant the last red disappears); call with the
    // ball that was just potted to try to move past it - if that ball wasn't actually the one
    // required, the ball on simply doesn't move. Skips over any colour that's already missing from
    // the table (potted illegally earlier and never respawned) instead of getting stuck asking for it.
    private void AdvanceColourSequence(BallType? justLegallyPotted = null)
    {
        if (justLegallyPotted.HasValue && justLegallyPotted.Value != currentTargetColour) return;

        do
        {
            colourSequenceIndex++;
            if (colourSequenceIndex >= ColourSequence.Length)
            {
                currentTargetColour = null;
                EndFrame();
                return;
            }
            currentTargetColour = ColourSequence[colourSequenceIndex];
        }
        while (!IsColourOnTable(currentTargetColour.Value));

        targetState = TargetBallState.Colour;
        if (debugLogging) Debug.Log($"[GMDebug] Colour sequence advanced - next up: {currentTargetColour}");
    }

    // The black has gone down off the end of the colour sequence - the frame is over.
    // Highest score wins; equal scores are reported as a tie (the real respotted-black
    // tie-break procedure is not modelled).
    // forcedWinner overrides the normal score comparison - used by the Normal scene's player
    // turn-limit (see CountPlayerTurnAndMaybeEndFrame) to end the frame with a fixed winner
    // regardless of score. Every other caller leaves it null and gets the original behaviour.
    private void EndFrame(int? forcedWinner = null)
    {
        if (frameOver) return;
        frameOver = true;

        confirmMode = false;
        inputLocked = true;
        strikeRequested = false;
        if (awaitingPlacement) EndPlacement(); // a foul on the final black still ends the frame

        int winner;
        if (forcedWinner.HasValue)
        {
            winner = forcedWinner.Value;
        }
        else
        {
            winner = -1;
            if (playerScores[0] > playerScores[1]) winner = 0;
            else if (playerScores[1] > playerScores[0]) winner = 1;
        }

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

    // Normal scene only. Counts one genuine end of the PLAYER's (non-AI) turn - a legal miss or a
    // foul, i.e. exactly the two PassTurn() call sites below in EvaluateFoul(). Deliberately NOT
    // hooked into PassTurn() itself: ChooseFoulPlayAgain() also calls PassTurn() to resolve a foul
    // decision, and that flip is an administrative follow-up to a turn that already ended here, not a
    // second turn ending - counting it there would burn the player's turn budget just for choosing
    // "make them play again" without ever taking a shot.
    // Returns true if hitting the cap just force-ended the frame, so the caller can skip the normal
    // turn-pass/foul-decision flow below it.
    private bool CountPlayerTurnAndMaybeEndFrame()
    {
        if (!enforcePlayerTurnLimit || currentPlayerIndex == aiPlayerIndex) return false;

        playerTurnsTaken++;
        if (playerTurnsTaken < maxPlayerTurns) return false;

        if (debugLogging) Debug.Log($"[GMDebug] Player used all {maxPlayerTurns} turns - AI wins by turn limit.");
        EndFrame(aiPlayerIndex);
        return true;
    }

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

    // The balls that currently count as "ball-on" per the normal (non-free-ball) rule - every red
    // while on Red, or just the specific nominated/sequence colour while on Colour. Mirrors
    // DefaultAimTargeting.IsLegalTarget / SnookerAI.CollectLegalTargets's own logic; used here only
    // for the free-ball snooker check below, not for aiming or shot selection.
    private IEnumerable<Rigidbody> CurrentBallsOn()
    {
        foreach (var ball in balls)
        {
            if (ball == null || !ball.gameObject.activeInHierarchy || ball == cueBall) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id == null) continue;

            if (targetState == TargetBallState.Red) { if (id.Type == BallType.Red) yield return ball; }
            else if (currentTargetColour.HasValue && id.Type == currentTargetColour.Value) yield return ball;
        }
    }

    // How many directions around a ball-on's surface to sample for a clear line from the cue ball -
    // see EvaluateFreeBallEligibility.
    private const int FreeBallSightSamples = 16;

    // FREE_BALL.md: called once the position resulting from a just-confirmed foul is actually final
    // (see the pendingFreeBallCheck callers) - checks whether the incoming player is snookered on
    // every current ball-on, i.e. no ball-on is reachable by any straight line from the cue ball's
    // resting position. If every single one is blocked from every sampled angle, a free ball is
    // awarded for their next shot.
    //
    // Deliberately direct-line-of-sight only - the doc's "or off a cushion" escape route needs real
    // cushion-plane reflection geometry this project doesn't expose (Cue's cushion raycasts are
    // internal, not exposed as plane data GameManager can reuse). Skipping it means this can award a
    // free ball in some cases where a real referee, accounting for a bank-shot escape, would not - it
    // never does the opposite (call a player snookered who actually has a clear direct pot), so the
    // simplification only ever gives, never wrongly withholds.
    private void EvaluateFreeBallEligibility()
    {
        freeBallAvailable = false;
        if (cue == null || cueBall == null) return;

        Vector3 cueBallPos = cueBall.transform.position;
        float ballDiameter = cue.CueBallRadius * 2f;
        bool anyBallOn = false;

        foreach (var ball in CurrentBallsOn())
        {
            anyBallOn = true;
            for (int i = 0; i < FreeBallSightSamples; i++)
            {
                float angle = i * (360f / FreeBallSightSamples) * Mathf.Deg2Rad;
                Vector3 approach = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Vector3 ghost = ball.transform.position + approach * ballDiameter;
                if (cue.IsPathClear(cueBallPos, ghost, ball, cueBall, true))
                    return; // at least one ball-on is reachable from at least one angle - not snookered
            }
        }

        freeBallRedsRemained = RedsRemainingOnTable() > 0;
        freeBallAvailable = anyBallOn; // vacuously false if there was no ball-on at all to test
        if (freeBallAvailable && debugLogging)
            Debug.Log("[GMDebug] FREE BALL - incoming player is snookered on every ball-on as a direct result of the foul.");
    }

    // 6.1-6.4: single end-of-shot foul decision, run once per completed shot.
    private void EvaluateFoul()
    {
        lastShotWasFoul = false;
        lastFoulReason = FoulReason.None;
        bool cueBallPotted = cueBall != null && PottedThisShot.Contains(cueBall);
        BallType? firstType = GetBallType(firstBallContacted);

        // Snapshot this shot's free-ball state for EvaluateShotResult (runs right after this) before
        // the unconditional consume at the bottom clears it for the *next* shot.
        bool consumingFreeBall = freeBallAvailable && freeBallNomination.HasValue;
        lastShotFreeBallNomination = consumingFreeBall ? freeBallNomination : null;
        lastShotFreeBallRedsRemained = freeBallRedsRemained;

        bool legal;
        int points;

        if (consumingFreeBall)
        {
            // FREE_BALL.md: for this one shot, the nominated ball substitutes as ball-on - everything
            // else about how a shot is judged (cue ball potted/no contact = foul, wrong first contact
            // = foul, single legal pot = legal, miss = legal no-score, anything else = foul) mirrors
            // the Red/Colour branches below exactly, just checked against the nomination instead of
            // the normal target, and scored per the doc's substitution rule (1 point like a red while
            // reds remain, or the real next colour's value once they're gone) instead of BallValue
            // for the normal on-ball.
            if (!freeBallRedsRemained && !currentTargetColour.HasValue)
            {
                Debug.LogWarning("[GMDebug] EvaluateFoul: free ball active in colours-only phase with no " +
                                  "currentTargetColour - skipping foul check.");
                return;
            }

            BallType nominated = freeBallNomination.Value;
            int nominatedValue = freeBallRedsRemained ? BallValue[BallType.Red] : BallValue[currentTargetColour.Value];

            if (cueBallPotted || firstBallContacted == null || !firstType.HasValue)
            {
                legal = false;
                points = Mathf.Max(4, nominatedValue);
                lastFoulReason = cueBallPotted ? FoulReason.CueBallFoul : FoulReason.NoBallHitFoul;
            }
            else if (firstType != nominated)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[firstType.Value]);
                lastFoulReason = FoulReason.WrongBallFoul;
            }
            else if (PottedThisShot.Count == 1 && GetBallType(PottedThisShot[0]) == nominated)
            {
                legal = true;
                points = nominatedValue;
            }
            else if (PottedThisShot.Count == 0)
            {
                legal = true;
                points = 0;
            }
            else
            {
                legal = false;
                points = Mathf.Max(4, nominatedValue);
            }
        }
        else if (targetState == TargetBallState.Red)
        {
            // 6.2 - Player On Red
            // !firstType.HasValue means the recorded contact wasn't an identifiable ball, which
            // is scored the same as hitting nothing - and keeps firstType.Value below safe.
            if (cueBallPotted || firstBallContacted == null || !firstType.HasValue)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[BallType.Red]); // 6.5: potting cue ball / hitting nothing
                lastFoulReason = cueBallPotted ? FoulReason.CueBallFoul : FoulReason.NoBallHitFoul;
            }
            else if (firstType != BallType.Red)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[firstType.Value]);
                lastFoulReason = FoulReason.WrongBallFoul;
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
                lastFoulReason = cueBallPotted ? FoulReason.CueBallFoul : FoulReason.NoBallHitFoul;
            }
            else if (firstType != target)
            {
                legal = false;
                points = Mathf.Max(4, BallValue[firstType.Value]);
                lastFoulReason = FoulReason.WrongBallFoul;
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
                if (CountPlayerTurnAndMaybeEndFrame()) return; // Legendary: player's turn cap reached
                PassTurn();
            }
        }
        else
        {
            lastShotWasFoul = true;
            int pointsAwardedTo = OpponentIndex;
            AwardPoints(pointsAwardedTo, points);
            if (CountPlayerTurnAndMaybeEndFrame()) return; // Legendary: player's turn cap reached
            PassTurn();

            if (debugLogging) Debug.Log($"[GMDebug] FOUL: {points} pts to Player {pointsAwardedTo} - " +
                                          $"awaiting their play/play-again decision.");

            // FOUL_PLAY_AGAIN_RULE.md: the non-offending player (now currentPlayerIndex, after
            // PassTurn above) decides whether to play on or send the fouling player back in,
            // BEFORE any placement-in-D or normal-aim flow starts. Ball in hand (driven by the
            // cue ball being off the table, not by "a foul happened" - BALL_PLACEMENT_D.md
            // section 1) is deferred until the decision resolves, since it applies to whichever
            // player ends up actually playing next - see ChooseFoulPlay/ChooseFoulPlayAgain.
            awaitingFoulDecision = true;
            foulDecisionForPlayerIndex = currentPlayerIndex;
            foulDecisionCueBallPotted = cueBallPotted;
            RefreshFoulDecisionPanel();

            // FREE_BALL.md: flag for a snooker check once the incoming player's cue ball position is
            // actually final - see EvaluateFreeBallEligibility and its callers (ChooseFoulPlay,
            // ChooseFoulPlayAgain, ConfirmPlacement). Not evaluated right here since a potted cue ball
            // means ball-in-hand, and the real resting position isn't known until they've placed it.
            pendingFreeBallCheck = true;
        }

        // One-shot opportunity: consumed the moment it's been evaluated, whether the player used it,
        // ignored it in favour of a normal/safety shot, or fouled on it - never carries over.
        freeBallAvailable = false;
        freeBallNomination = null;
    }

    // ======================================================================
    // ----- Foul Play/Play-Again Decision (FOUL_PLAY_AGAIN_RULE.md) -----
    // ======================================================================

    public bool IsAwaitingFoulDecision => awaitingFoulDecision;
    public int FoulDecisionForPlayerIndex => foulDecisionForPlayerIndex;
    // The player who committed the foul - only meaningful while IsAwaitingFoulDecision is true,
    // since currentPlayerIndex is the non-offending decision-maker for that entire window.
    public int FoulingPlayerIndex => OpponentIndex;

    private void RefreshFoulDecisionPanel()
    {
        if (foulDecisionPanel != null)
            foulDecisionPanel.SetActive(awaitingFoulDecision);
    }

    // "Play" - take the next shot from the table as it lies (plus placement-in-D if the cue
    // ball was potted). Called by the human's Play button or the AI's own decision logic.
    public void ChooseFoulPlay()
    {
        if (!awaitingFoulDecision) return;
        if (BlockedAsHumanInputDuringAiTurn) return;

        if (debugLogging) Debug.Log($"[GMDebug] Player {foulDecisionForPlayerIndex} chooses to PLAY.");
        awaitingFoulDecision = false;
        RefreshFoulDecisionPanel();
        if (foulDecisionCueBallPotted) BeginPlacement();
        else TryResolvePendingFreeBallCheck();
    }

    // "Make [opponent] play again" - decline, sending the fouling player back in from the same
    // position. Reuses PassTurn() to flip currentPlayerIndex back; its Colour/Red reset check is
    // already a no-op here since the PassTurn call inside EvaluateFoul already applied it if it
    // was going to (targetState only resets Colour -> Red once per foul, and toggling
    // currentPlayerIndex a second time doesn't reopen that).
    public void ChooseFoulPlayAgain()
    {
        if (!awaitingFoulDecision) return;
        if (BlockedAsHumanInputDuringAiTurn) return;

        if (debugLogging) Debug.Log($"[GMDebug] Player {foulDecisionForPlayerIndex} makes Player " +
                                      $"{FoulingPlayerIndex} play again.");
        awaitingFoulDecision = false;
        RefreshFoulDecisionPanel();
        PassTurn();
        if (foulDecisionCueBallPotted) BeginPlacement();
        else TryResolvePendingFreeBallCheck();
    }

    // FREE_BALL.md: runs the snooker check exactly once, the moment the incoming player's cue ball
    // position is actually final - either here (no placement was needed) or from ConfirmPlacement
    // (placement was needed, so this waits for it instead of running against a stale position).
    // pendingFreeBallCheck is only ever true when EvaluateFoul just flagged it, so this is a no-op on
    // every other call to ConfirmPlacement (e.g. ordinary frame-start placement).
    private void TryResolvePendingFreeBallCheck()
    {
        if (!pendingFreeBallCheck) return;
        pendingFreeBallCheck = false;
        EvaluateFreeBallEligibility();
    }

    // ======================================================================
    // ----- Commentary System -----
    // ======================================================================

    // Works out which ball the cue is about to hit and how hard the resulting pot would be, using
    // the cue's current aim direction - called the instant a strike is actually taken (RequestStrike),
    // before any ball has moved. Also records whose turn it was and whether this is the break-off.
    // Then starts the shot's commentary running live, straight away - see PlayShotCommentary.
    private void PrepareCommentaryForShot()
    {
        // Safety fallback only: RequestStrike/ConfirmButtonPressed/MyTurnToAct all now wait for
        // IsCommentaryPlaying, so a new shot should never actually reach here while commentary is
        // still going. Kept in case some other path ever does, so two shots' commentary can't overlap.
        if (commentaryCoroutine != null) StopCoroutine(commentaryCoroutine);
        if (commentaryAudioSource != null) commentaryAudioSource.Stop();
        isCommentaryPlaying = false;
        shotHasResolved = false;

        pendingCommentaryIsBreakOff = shotsTakenThisFrame == 0;
        shotsTakenThisFrame++;
        pendingCommentaryWasAiTurn = IsAiTurn;
        pendingCommentaryObjectBall = null;

        if (cue != null && cueBall != null)
        {
            Rigidbody objectBall = FindLikelyObjectBall(out Vector3 ghostContactPoint);
            if (objectBall != null) // aim doesn't line up with any ball - stays a plain Miss if it misses
            {
                pendingCommentaryObjectBall = objectBall;
                pendingCommentaryShotType = ClassifyShotType(objectBall, ghostContactPoint);
            }
        }

        if (pendingCommentaryIsBreakOff || Time.timeScale <= 0f) return; // no commentary at all for this shot

        isCommentaryPlaying = true;
        commentaryCoroutine = StartCoroutine(PlayShotCommentary());
    }

    // Finds the first ball the cue ball's current aim direction would actually contact, using the
    // same ghost-ball geometry the aim-prediction line already draws (see Cue.cs), done here with
    // plain vector math instead of a physics raycast so GameManager doesn't need Cue's private
    // layer masks. Outputs the ghost ball's centre at the moment of contact, needed to work out
    // which way the object ball goes afterwards.
    private Rigidbody FindLikelyObjectBall(out Vector3 ghostContactPoint)
    {
        ghostContactPoint = Vector3.zero;
        Vector3 origin = cueBall.transform.position;
        Vector3 aimDir = cue.CurrentAimForward.normalized;
        float cueRadius = RadiusOf(cueBall);

        Rigidbody bestBall = null;
        float bestContactDist = float.PositiveInfinity;

        foreach (var ball in balls)
        {
            if (ball == null || ball == cueBall || !ball.gameObject.activeInHierarchy) continue;

            Vector3 toBall = ball.transform.position - origin;
            float alongRay = Vector3.Dot(toBall, aimDir);
            if (alongRay <= 0f) continue; // behind the cue ball - can't be the first thing it hits

            Vector3 closestPoint = origin + aimDir * alongRay;
            float perpDist = Vector3.Distance(closestPoint, ball.transform.position);
            float combinedRadius = cueRadius + RadiusOf(ball);
            if (perpDist >= combinedRadius) continue; // aim line misses this ball entirely

            float contactDist = alongRay - Mathf.Sqrt(combinedRadius * combinedRadius - perpDist * perpDist);
            if (contactDist < bestContactDist)
            {
                bestContactDist = contactDist;
                bestBall = ball;
                ghostContactPoint = origin + aimDir * contactDist;
            }
        }

        return bestBall;
    }

    // Difficult cut first, then Long, then Easy (SHOT_COMMENTARY priority rule). Cut angle is the
    // angle between (cue ball -> object ball) and (object ball -> pocket); distance is cue-to-object
    // plus object-to-pocket. The pocket used is whichever one best matches the direction the object
    // ball actually travels off this contact (ghost-ball centre -> object ball centre), not just the
    // raw aim direction, so a thin cut still picks a sensible pocket.
    private CommentaryShotType ClassifyShotType(Rigidbody objectBall, Vector3 ghostContactPoint)
    {
        Vector3 origin = cueBall.transform.position;
        Vector3 objectPos = objectBall.transform.position;
        Vector3 travelDir = (objectPos - ghostContactPoint).normalized;

        Transform bestPocket = null;
        float bestAngleToPocket = float.PositiveInfinity;
        Vector3 bestObjectToPocket = Vector3.zero;
        foreach (var pocket in pocketTransforms)
        {
            if (pocket == null) continue;
            Vector3 objectToPocket = pocket.position - objectPos;
            float angle = Vector3.Angle(travelDir, objectToPocket);
            if (angle < bestAngleToPocket)
            {
                bestAngleToPocket = angle;
                bestPocket = pocket;
                bestObjectToPocket = objectToPocket;
            }
        }
        if (bestPocket == null) return CommentaryShotType.EasyPot; // no pockets found - safe fallback

        float cutAngle = Vector3.Angle(objectPos - origin, bestObjectToPocket);
        float totalDistance = Vector3.Distance(origin, objectPos) + bestObjectToPocket.magnitude;

        if (cutAngle > commentaryCutAngleThreshold) return CommentaryShotType.CutPot;
        if (totalDistance > commentaryLongPotDistance) return CommentaryShotType.LongPot;
        return CommentaryShotType.EasyPot;
    }

    // Runs once per shot, after EvaluateFoul/EvaluateShotResult above (so LastShotWasFoul/LastFoulReason/
    // PottedThisShot are all final). Just flips a flag - PlayShotCommentary has been running live since
    // the shot was struck and is the one actually waiting on this, so the result plays the instant it's
    // known instead of only once the whole shot has visibly finished.
    private void PlayCommentaryForFinishedShot()
    {
        shotHasResolved = true;
    }

    // Decides which result clips play once the shot is known to have resolved, in order. A foul plays
    // only its own single clip (CueBallFoul > WrongBallFoul > NoBallHitFoul priority, already reflected
    // in lastFoulReason by EvaluateFoul). Otherwise: the pot/miss pool for this shot type, then
    // AfterRedBall if a red was legally potted. The ball-name clip isn't here any more - it already
    // played live, the instant the shot was struck (see PlayShotCommentary).
    private List<AudioClip> BuildCommentaryQueue()
    {
        var queue = new List<AudioClip>();

        if (lastShotWasFoul)
        {
            AudioClip foulClip = null;
            if (lastFoulReason == FoulReason.CueBallFoul) foulClip = CueBallFoul;
            else if (lastFoulReason == FoulReason.WrongBallFoul) foulClip = WrongBallFoul;
            else if (lastFoulReason == FoulReason.NoBallHitFoul) foulClip = NoBallHitFoul;
            if (foulClip != null) queue.Add(foulClip);
            return queue;
        }

        bool potted = pendingCommentaryObjectBall != null && PottedThisShot.Contains(pendingCommentaryObjectBall);
        AudioClip[] pool;
        if (potted)
        {
            switch (pendingCommentaryShotType)
            {
                case CommentaryShotType.LongPot: pool = pendingCommentaryWasAiTurn ? AI_LongPot : Player_LongPot; break;
                case CommentaryShotType.CutPot: pool = pendingCommentaryWasAiTurn ? AI_CutPot : Player_CutPot; break;
                default: pool = pendingCommentaryWasAiTurn ? AI_EasyPot : Player_EasyPot; break;
            }
        }
        else
        {
            pool = pendingCommentaryWasAiTurn ? AI_Miss : Player_Miss;
        }
        if (pool != null && pool.Length > 0)
        {
            AudioClip picked = pool[PickNonRepeatingCommentaryIndex(pool)];
            if (picked != null) queue.Add(picked); // an unfilled slot in the array - skip quietly
        }

        bool redLegallyPotted = PottedThisShot.Exists(b => GetBallType(b) == BallType.Red);
        if (redLegallyPotted && AfterRedBall != null) queue.Add(AfterRedBall);

        return queue;
    }

    // Maps a ball colour to its name clip (Change 2). Cue has no name clip, so it simply isn't in
    // this list - null falls through and gets skipped in BuildCommentaryQueue.
    private AudioClip BallNameClip(BallType? type)
    {
        if (!type.HasValue) return null;
        switch (type.Value)
        {
            case BallType.Red: return Red;
            case BallType.Yellow: return Yellow;
            case BallType.Green: return Green;
            case BallType.Brown: return Brown;
            case BallType.Blue: return Blue;
            case BallType.Pink: return Pink;
            case BallType.Black: return Black;
            default: return null;
        }
    }

    // Runs for the whole shot, live with the action instead of waiting for it to finish:
    // 1) plays the ball-name clip immediately - it's predicted from the pre-shot aim classification
    //    (pendingCommentaryObjectBall), the only thing knowable the instant the cue strikes;
    // 2) then waits for shotHasResolved (set by PlayCommentaryForFinishedShot once foul/pot/miss is
    //    actually known - that can't happen any earlier than the real result exists);
    // 3) then plays the result clip(s) after a short commentaryDelay beat.
    // A safety timeout (commentaryTimeoutSeconds), measured from when the shot was struck, covers
    // both the wait and the playback, so the next shot can never be blocked forever. IsCommentaryPlaying
    // stays true for the whole thing and is only cleared once, right at the end - see
    // IsAimFrozen/ConfirmButtonPressed/RequestStrike/MyTurnToAct, which is what actually holds the next
    // shot back.
    private IEnumerator PlayShotCommentary()
    {
        var source = GetCommentaryAudioSource();
        float deadline = Time.time + commentaryTimeoutSeconds;

        AudioClip nameClip = playBallNameClip ? BallNameClip(GetBallType(pendingCommentaryObjectBall)) : null;
        if (nameClip != null)
        {
            source.Stop();
            source.PlayOneShot(nameClip);
            yield return new WaitForSeconds(nameClip.length);
        }

        while (!shotHasResolved && Time.time < deadline) yield return null;

        List<AudioClip> resultQueue = BuildCommentaryQueue();
        pendingCommentaryObjectBall = null; // consumed - this shot's result can't be reused for the next one

        if (resultQueue.Count > 0 && Time.time < deadline)
        {
            yield return new WaitForSeconds(commentaryDelay);
            foreach (var clip in resultQueue)
            {
                if (clip == null) continue; // belt-and-braces - skip an empty slot quietly, no errors

                if (Time.time > deadline)
                {
                    Debug.LogWarning($"[GMDebug] Commentary passed its {commentaryTimeoutSeconds}s " +
                                      "safety timeout - skipping the rest so the next shot isn't blocked forever.");
                    break;
                }

                source.Stop(); // cut whatever's still playing before starting the next line
                source.PlayOneShot(clip);
                yield return new WaitForSeconds(clip.length);
            }
        }

        isCommentaryPlaying = false;
    }

    // Random index into pool, excluding whichever index played last time from THIS SAME pool array -
    // tracked per-pool so the 8 pools never influence each other. Same trick PocketTrigger uses for
    // picking pot VFX.
    private int PickNonRepeatingCommentaryIndex(AudioClip[] pool)
    {
        if (pool.Length == 1) { lastCommentaryIndex[pool] = 0; return 0; }

        lastCommentaryIndex.TryGetValue(pool, out int last);
        int index;
        do { index = UnityEngine.Random.Range(0, pool.Length); }
        while (index == last);

        lastCommentaryIndex[pool] = index;
        return index;
    }

    // Lazily resolves/creates the dedicated commentary AudioSource, same pattern PocketTrigger uses
    // for its own pot-sound source.
    private AudioSource GetCommentaryAudioSource()
    {
        if (commentaryAudioSource == null)
        {
            commentaryAudioSource = gameObject.AddComponent<AudioSource>();
            commentaryAudioSource.playOnAwake = false;
        }
        return commentaryAudioSource;
    }
}
