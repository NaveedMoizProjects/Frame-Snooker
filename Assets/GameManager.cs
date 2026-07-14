using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    // ---------------- Singleton ----------------
    // Dev doc requires a central GameManager singleton owning turn state.
    public static GameManager Instance { get; private set; }

    [Header("Game Settings")]
    [SerializeField]
    private List<Rigidbody> balls; // List of all balls in the game

    [Header("Strike Settings")]
    [SerializeField] private float strikeForceMultiplier = 10000f; // was a hardcoded magic number

    [Header("Ball Rest Thresholds")]
    [Tooltip("Above this speed, a ball counts as 'moving' and blocks the next shot.")]
    [SerializeField] private float moveThreshold = 0.01f;
    [Tooltip("Below this speed, a moving ball is snapped to a full stop to kill physics jitter.")]
    [SerializeField] private float snapToZeroThreshold = 0.09f;

    protected bool nextplay = false;
    protected float StrikeForce = 30f; // force applied to the cue ball when hit
    protected bool isstrike = false;   // flag to send the strike to the cue ball
    protected bool confirmstrike = false; // flag to confirm the strike

    private CameraSwitching cameraSwitching;
    private bool wasMovingLastCheck = false;

    // Event-driven hook per architecture doc (Section 2: "Event-driven, not polling").
    // Fires exactly once, on the tick balls transition from moving -> fully stopped.
    public event System.Action OnAllBallsStopped;

    void Awake()
    {
        // Basic singleton guard so only one GameManager ever owns turn state.
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Duplicate GameManager found in scene - destroying the extra one.", this);
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        // Cursor.lockState = CursorLockMode.Locked; // left disabled for mobile/editor testing
        cameraSwitching = FindObjectOfType<CameraSwitching>();
        if (cameraSwitching == null)
            Debug.LogWarning("GameManager: No CameraSwitching found in scene - Cam1/Cam2/Cam3 will do nothing.", this);
    }

    public void Cam1() => cameraSwitching?.SwitchToTopDownCamera();
    public void Cam2() => cameraSwitching?.SwitchToThirdPersonCamera();
    public void Cam3() => cameraSwitching?.SwitchToFirstPersonCamera();

    public bool isNextPlay() => nextplay;

    // Physics-derived state (ball velocity) must be evaluated on the physics tick,
    // not the render frame - this is what makes shot resolution frame-rate independent
    // (dev doc Section 2: "Fixed timestep physics").
    void FixedUpdate()
    {
        CheckNextPlay(balls);
    }

    // First we check each ball's speed.
    // Any ball moving above threshold => next play blocked.
    // Any ball crawling below the snap threshold => forced to a hard stop (removes jitter).
    public void CheckNextPlay(List<Rigidbody> balls)
    {
        bool anyMoving = false;

        // NOTE: intentionally does NOT break early - the previous version stopped scanning
        // the instant it found one moving ball, so any other near-stationary ball never got
        // its snap-to-zero cleanup applied until it happened to be first in the list.
        // At 22 balls max this full scan is negligible cost either way.
        foreach (Rigidbody ball in balls)
        {
            if (ball == null) continue; // guards against a potted/pooled ball reference

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

    public bool checkconfirmstrike() => confirmstrike;

    public void buttonstrike()
    {
        confirmstrike = !confirmstrike;
        Debug.Log("Confirmation of strike is " + confirmstrike);
    }

    public void SetConfirmStrike(bool val)
    {
        Debug.Log("Confirmation of strike is " + val);
        confirmstrike = val;
    }

    public float GetStrikeForce() => StrikeForce;

    public void SetStrikeForce(float force)
    {
        if (force <= 0f)
            force += 0.1f;
        StrikeForce = force * strikeForceMultiplier;
    }

    public bool GetIsStrike() => isstrike;

    public void SetIsStrike()
    {
        Debug.Log("The Ball is striked with force: " + StrikeForce);
        isstrike = true;
    }

    public void SetIsStrike(bool strike) => isstrike = strike;
}



