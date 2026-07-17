using System;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    // ---------------- Singleton ----------------
    public static GameManager Instance { get; private set; }

    [Header("Game Settings")]
    [SerializeField] private List<Rigidbody> balls; // all balls in the game

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
    }

    void Start()
    {
        cameraSwitching = FindObjectOfType<CameraSwitching>();
        if (cameraSwitching == null)
            Debug.LogWarning("GameManager: No CameraSwitching found in scene.", this);

        Debug.Log($"GameManager initialized: baseStrikeForce={baseStrikeForce} => GetStrikeForce()={GetStrikeForce()}");
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

    public void RequestStrike() => strikeRequested = true;
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
}