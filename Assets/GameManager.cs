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

    [Header("TEMP DEBUG - delete after fixing")]
    [SerializeField] private bool debugLogging = true;
    private float debugLogTimer2 = 0f;

    // state
    private bool nextplay = false;
    private bool strikeRequested = false;
    private bool confirmMode = false;
    private bool inputLocked = false;

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

        // Phase 3: once a shot has fully settled, work out what got potted and apply
        // colour/scoring rules. Runs after the unlock above but order between the two
        // doesn't matter - they touch unrelated state.
        OnAllBallsStopped += EvaluateShotResult;
    }

    void Start()
    {
        cameraSwitching = FindObjectOfType<CameraSwitching>();
        if (cameraSwitching == null)
            Debug.LogWarning("GameManager: No CameraSwitching found in scene.", this);

        Debug.Log($"GameManager initialized: baseStrikeForce={baseStrikeForce} => GetStrikeForce()={GetStrikeForce()}");

        EnsureColourNominationUi();
    }

    // Phase 5 UI is required after potting a red (section 5.1). Auto-attach once if missing.
    private void EnsureColourNominationUi()
    {
        if (FindObjectOfType<ColourNominationUI>() != null) return;

        var canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            Debug.LogWarning("GameManager: No Canvas found - add ColourNominationUI to a Canvas so players can nominate colours after potting a red.", this);
            return;
        }

        canvas.gameObject.AddComponent<ColourNominationUI>();
        if (debugLogging) Debug.Log("[GMDebug] ColourNominationUI auto-added to Canvas.");
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
        CheckNextPlay(balls);
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

            float speed = ball.velocity.magnitude;
            if (speed > moveThreshold)
            {
                anyMoving = true;

                if (shouldLogThisTick)
                    Debug.Log($"[GMDebug] {ball.gameObject.name} is moving at speed {speed:F4} pos={ball.transform.position}");

                if (speed < snapToZeroThreshold)
                {
                    ball.velocity = Vector3.zero;
                    ball.angularVelocity = Vector3.zero;
                }
            }
        }

        nextplay = !anyMoving;

        if (wasMovingLastCheck && !anyMoving)
            OnAllBallsStopped?.Invoke();

        wasMovingLastCheck = anyMoving;
    }

    // ----- Confirm / Strike API (explicit, UI-friendly) -----
    public bool IsConfirmMode => confirmMode;
    public bool IsStrikeRequested => strikeRequested;
    public bool IsInputLocked => inputLocked;

    // Called by the single on-screen button.
    // First press enters Confirm mode (locks input). Second press requests the strike.
    public void ConfirmButtonPressed()
    {
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

        if (!confirmMode)
        {
            confirmMode = true;
            inputLocked = true;
            Debug.Log("Confirm pressed: input locked. Button should now say 'Strike!'");
        }
        else
        {
            RequestStrike();
            Debug.Log("Confirm pressed again: strike requested.");
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
        strikeRequested = true;
        PottedThisShot.Clear();
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
                ball.transform.position = cueBallRespawnPoint.position;
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

    // ======================================================================
    // ----- Phase 3: Colour Logic, Selection & Scoring (doc section 5) -----
    // ======================================================================

    public enum TargetBallState { Red, Colour }

    [Header("Phase 3 - Colour Logic & Scoring")]
    [Tooltip("Starts on Red per 5.1 (assumes reds remain on table at frame start).")]
    [SerializeField] private TargetBallState targetState = TargetBallState.Red;

    // Set by the nomination UI (OnColourNominated). Only meaningful while
    // targetState == Colour AND reds remain on table - once reds run out the
    // sequence below drives currentTargetColour automatically, no nomination needed.
    private BallType? currentTargetColour = null;

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

    // Runs once per completed shot (subscribed to OnAllBallsStopped in Awake).
    // NOTE: this is deliberately simple - it does not yet check whether the shot was
    // LEGAL (wrong ball hit first, cue potted, etc). That's the Foul Table in Phase 4;
    // this just applies section 5's colour/respawn/scoring rules to whatever was potted.
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
                if (targetState == TargetBallState.Red)
                {
                    AwardPoints(currentPlayerIndex, BallValue[BallType.Red]);
                    targetState = TargetBallState.Colour;
                    currentTargetColour = null; // must be nominated before next shot (or auto-set if reds now gone)
                    OnTargetChanged?.Invoke(targetState, currentTargetColour);
                }
                else
                {
                    // Red potted while target was Colour - illegal in real snooker.
                    // Left for Phase 4's foul table to actually penalize; just flagging here.
                    Debug.LogWarning("[GMDebug] Red potted while on Colour - Phase 4 foul table will need to handle this.");
                }
            }
            else
            {
                ResolveColourPot(identity, potted);
            }
        }
    }

    // 5.3 Respawn logic.
    private void ResolveColourPot(BallIdentity identity, Rigidbody colourBall)
    {
        AwardPoints(currentPlayerIndex, BallValue[identity.Type]);

        if (RedsRemainingOnTable() > 0)
        {
            // [Assumed, polish item] Spot-conflict rule (real snooker: nearest available spot
            // up the table if occupied) is not handled yet - straight respot to SpawnPosition
            // for now, per the doc's note that this edge case is rare and can be revisited later.
            colourBall.transform.position = identity.SpawnPosition;
            colourBall.velocity = Vector3.zero;
            colourBall.angularVelocity = Vector3.zero;
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
            Debug.Log("[GMDebug] Colour sequence complete - frame finished. (End-of-frame handling: Phase 7.)");
        }
    }
}