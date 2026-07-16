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
    [SerializeField] private float strikeForceMultiplier = 10000f;
    [SerializeField] private float baseStrikeForce = 30f; // Inspector control for testing

    [Header("Ball Rest Thresholds")]
    [Tooltip("Above this speed, a ball counts as 'moving' and blocks the next shot.")]
    [SerializeField] private float moveThreshold = 0.01f;
    [Tooltip("Below this speed, a moving ball is snapped to a full stop to kill physics jitter.")]
    [SerializeField] private float snapToZeroThreshold = 0.09f;

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
    }

    void Start()
    {
        cameraSwitching = FindObjectOfType<CameraSwitching>();
        if (cameraSwitching == null)
            Debug.LogWarning("GameManager: No CameraSwitching found in scene.", this);

        Debug.Log($"GameManager initialized: baseStrikeForce={baseStrikeForce} strikeForceMultiplier={strikeForceMultiplier} => GetStrikeForce()={GetStrikeForce()}");
    }

    public void Cam1() => cameraSwitching?.SwitchToTopDownCamera();
    public void Cam2() => cameraSwitching?.SwitchToThirdPersonCamera();
    public void Cam3() => cameraSwitching?.SwitchToFirstPersonCamera();

    public bool isNextPlay() => nextplay;

    void FixedUpdate()
    {
        CheckNextPlay(balls);
    }

    // ----- Ball motion / next-play logic -----
    public void CheckNextPlay(List<Rigidbody> balls)
    {
        bool anyMoving = false;

        foreach (Rigidbody ball in balls)
        {
            if (ball == null) continue;

            float speed = ball.velocity.magnitude;
            if (speed > moveThreshold)
            {
                anyMoving = true;

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
        // Only allow confirming when table is ready for a shot
        if (!nextplay)
        {
            Debug.Log("Cannot confirm while balls are moving.");
            return;
        }

        if (!confirmMode)
        {
            // Enter confirm mode: lock input, change UI to "Strike!"
            confirmMode = true;
            inputLocked = true;
            Debug.Log("Confirm pressed: input locked. Button should now say 'Strike!'");
        }
        else
        {
            // Already confirmed: request strike (do not clear confirmMode here).
            RequestStrike();
            Debug.Log("Confirm pressed again: strike requested.");
        }
    }

    // Request/clear strike request (Cue consumes these)
    public void RequestStrike()
    {
        strikeRequested = true;
    }

    public void ClearStrikeRequest()
    {
        strikeRequested = false;
    }

    // Called by Cue after applying force to clear confirm and input lock
    public void ClearConfirmMode()
    {
        confirmMode = false;
        inputLocked = false;
    }

    // Strike force accessors - computed live so inspector changes take effect immediately
    public float GetStrikeForce() => baseStrikeForce * strikeForceMultiplier;

    // Set base strike force (inspector/slider can call this)
    public void SetStrikeForce(float force)
    {
        if (force <= 0f) force = 0.1f;
        baseStrikeForce = force;
        Debug.Log($"Strike base force set to: {baseStrikeForce} => GetStrikeForce()={GetStrikeForce()}");
    }
}