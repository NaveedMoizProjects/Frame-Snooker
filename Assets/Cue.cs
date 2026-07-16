using System.Collections.Generic;
using UnityEngine;

public class Cue : MonoBehaviour
{
    // ---------------- Game refs ----------------
    [Header("Game Manager")]
    [Tooltip("Optional: if left empty the GameManager.Instance will be used.")]
    [SerializeField] protected GameManager gameManager;
    [SerializeField] protected GameObject Cueball;

    [Header("Cue Stick Refs")]
    [SerializeField] private GameObject CueStick;     // visual stick (placeholder only)
    [SerializeField] private GameObject cuestickref;  // empty whose +Z points to cue ball

    // ---------------- Physics ----------------
    [Header("Cue Ball Rigidbody Settings")]
    [SerializeField] private float Mass = 1.0f;
    [SerializeField] private float Drag = 0.5f;
    [SerializeField] private float AngularDrag = 0.5f;

    // ---------------- Strike Control (Inspector Testing) ----------------
    [Header("Strike Control (Inspector Testing)")]
    [Range(0.01f, 15f)]
    [SerializeField] private float forceMultiplier = 1.0f; // scales the final force (live)
    [Range(-45f, 45f)]
    [SerializeField] private float angleOffsetDegrees = 0f; // horizontal English
    [Range(-30f, 30f)]
    [SerializeField] private float verticalAngleDegrees = 0f; // draw/follow
    [SerializeField] private bool applySpinTorque = true; // apply rotational force

    // ---------------- Prediction lines (kept simple) ----------------
    [Header("Prediction Lines")]
    [SerializeField] private LineRenderer aimLineCue;     // cue-ball path
    [SerializeField] private LineRenderer aimLineObject;  // object-ball path
    [SerializeField] private float maxDistance = 60f;
    [SerializeField] private float maxNoHitLength = 8f;

    [Header("Contact Stub Settings (both red & white)")]
    [SerializeField] private float contactStubLength = 0.35f;

    // ---------------- Layers ----------------
    [Header("Raycast Layers")]
    [SerializeField] private LayerMask ballLayer;
    [SerializeField] private LayerMask tableLayer;
    [SerializeField] private LayerMask pocketLayer;
    [SerializeField] private bool stopAtPockets = true;

    [Tooltip("Read from SphereCollider if <= 0")]
    [SerializeField] private float cueBallRadius = -1f;

    // ---------------- internals ----------------
    private bool isReadyToHit = false;
    private bool pendingStrike = false;
    private Rigidbody cueballRigidbody;

    // reuse buffer
    private readonly List<Vector3> aimPoints = new List<Vector3>(4);

    // debug
    [Header("TEMP DEBUG - delete after fixing")]
    [SerializeField] private bool debugLogging = false;
    private float debugLogTimer = 0f;

    void Start()
    {
        // prefer inspector reference but fall back to singleton
        if (gameManager == null && GameManager.Instance != null) gameManager = GameManager.Instance;

        if (!ValidateReferences())
        {
            enabled = false;
            return;
        }

        cueballRigidbody = Cueball.GetComponent<Rigidbody>() ?? Cueball.AddComponent<Rigidbody>();
        cueballRigidbody.mass = Mass;
        cueballRigidbody.drag = Drag;
        cueballRigidbody.angularDrag = AngularDrag;

        if (cueBallRadius <= 0f)
        {
            var sc = Cueball.GetComponent<SphereCollider>();
            if (sc)
            {
                var s = Cueball.transform.lossyScale;
                cueBallRadius = sc.radius * Mathf.Max(s.x, s.y, s.z);
            }
            else cueBallRadius = 0.0285f;
        }

        isReadyToHit = true;
    }

    private bool ValidateReferences()
    {
        bool ok = true;
        if (!gameManager) { Debug.LogError("Cue: GameManager not assigned.", this); ok = false; }
        if (!Cueball) { Debug.LogError("Cue: Cueball not assigned.", this); ok = false; }
        if (!CueStick) { Debug.LogError("Cue: CueStick not assigned.", this); ok = false; }
        if (!cuestickref) { Debug.LogError("Cue: cuestickref not assigned.", this); ok = false; }
        if (!aimLineCue) Debug.LogWarning("Cue: aimLineCue not assigned - prediction line will be skipped.", this);
        return ok;
    }

    void Update()
    {
        // ensure GameManager reference
        if (gameManager == null && GameManager.Instance != null) gameManager = GameManager.Instance;
        if (gameManager == null) return;

        bool confirmed = gameManager.IsConfirmMode;

        if (debugLogging)
        {
            debugLogTimer += Time.deltaTime;
            if (debugLogTimer > 0.5f)
            {
                debugLogTimer = 0f;
                Debug.Log($"[CueDebug] confirmMode={confirmed} | nextplay={gameManager.isNextPlay()} | strikeForce={gameManager.GetStrikeForce()} | forceMul={forceMultiplier} | angle={angleOffsetDegrees}°");
            }
        }

        // Prediction and strike logic remain here (visual stick control moved to CueVisualController).
        if (isReadyToHit && !confirmed)
            GenerateAimPrediction();

        // Only start strike when both confirmed AND strike requested
        bool strikeRequested = gameManager.IsStrikeRequested;
        if (strikeRequested && isReadyToHit && confirmed)
        {
            pendingStrike = true;
            isReadyToHit = false;
            Debug.Log("[Cue] Strike pending - will execute in FixedUpdate");
        }
        else
        {
            isReadyToHit = gameManager.isNextPlay();
        }
    }

    void FixedUpdate()
    {
        if (pendingStrike)
        {
            ApplyForceToCueBall();
            pendingStrike = false;
        }
    }

    // flatten to XZ
    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.zero : v.normalized;
    }

    // simplified, readable prediction: single straight segment
    private void GenerateAimPrediction()
    {
        if (aimLineCue == null) return;
        if (aimLineObject) aimLineObject.positionCount = 0;

        float tableY = Cueball.transform.position.y;
        Vector3 origin = Cueball.transform.position + Vector3.up * 0.01f;
        Vector3 dir = Flat(Cueball.transform.position - cuestickref.transform.position);
        if (dir == Vector3.zero) return;

        aimPoints.Clear();
        aimPoints.Add(new Vector3(origin.x, tableY, origin.z));

        int railMask = tableLayer | pocketLayer;

        RaycastHit ballHit;
        bool hasBall = Physics.SphereCast(origin, cueBallRadius, dir, out ballHit, maxDistance, ballLayer, QueryTriggerInteraction.Ignore);
        float dBall = hasBall ? ballHit.distance : float.PositiveInfinity;

        RaycastHit railHit;
        bool hasRail = Physics.Raycast(origin, dir, out railHit, maxDistance, railMask, QueryTriggerInteraction.Collide);
        float dRail = hasRail ? railHit.distance : float.PositiveInfinity;

        if (dBall < dRail)
        {
            Vector3 contact = origin + dir * dBall; contact.y = tableY;
            aimPoints.Add(contact);

            if (aimLineObject)
            {
                Vector3 objDir = Flat(-ballHit.normal);
                Vector3 bPt = contact + objDir * Mathf.Max(0.01f, contactStubLength);
                bPt.y = tableY;
                aimLineObject.positionCount = 2;
                aimLineObject.SetPosition(0, contact);
                aimLineObject.SetPosition(1, bPt);
            }
        }
        else if (hasRail)
        {
            Vector3 rp = railHit.point; rp.y = tableY;
            aimPoints.Add(rp);
        }
        else
        {
            Vector3 end = origin + dir * Mathf.Min(maxNoHitLength, maxDistance);
            end.y = tableY;
            aimPoints.Add(end);
        }

        aimLineCue.positionCount = aimPoints.Count;
        aimLineCue.SetPositions(aimPoints.ToArray());
    }

    private void ApplyForceToCueBall()
    {
        // Ensure we have GameManager access
        if (gameManager == null)
        {
            if (GameManager.Instance != null) gameManager = GameManager.Instance;
            if (gameManager == null) return;
        }

        // Get base direction from cuestick to ball
        Vector3 toBall = Cueball.transform.position - cuestickref.transform.position;
        float dist = toBall.magnitude;

        if (dist < 0.001f)
        {
            Debug.LogWarning("Cue too close to ball to apply force!");
            gameManager.ClearStrikeRequest();
            gameManager.ClearConfirmMode();
            return;
        }

        Vector3 normalizedDir = toBall.normalized;

        // Apply angle offsets (English/topspin)
        Vector3 forceDirection = CalculateForceDirection(normalizedDir);

        // Get base force and apply multiplier (live)
        float baseForceMagnitude = gameManager.GetStrikeForce();
        float finalForceMagnitude = baseForceMagnitude * forceMultiplier;

        // Ensure cue ball is dynamic
        if (cueballRigidbody.isKinematic)
        {
            Debug.LogWarning("Cueball Rigidbody is kinematic. Setting isKinematic = false so physics can move it.", this);
            cueballRigidbody.isKinematic = false;
        }

        // Diagnostics & ensure awake
        Debug.Log($"[Cue] Applying impulse. mass={cueballRigidbody.mass}, preVel={cueballRigidbody.velocity}, finalImpulse={finalForceMagnitude}", this);
        cueballRigidbody.WakeUp();

        // Apply as an impulse so mass/drag/angularDrag are respected.
        cueballRigidbody.AddForce(forceDirection * finalForceMagnitude, ForceMode.Impulse);

        // Apply spin if enabled (angular impulse)
        if (applySpinTorque)
        {
            Vector3 spinAxis = Vector3.Cross(forceDirection, Vector3.up);
            if (spinAxis.sqrMagnitude > 0.01f)
            {
                float spinMagnitude = finalForceMagnitude * 0.1f; // tuned factor
                cueballRigidbody.AddTorque(spinAxis.normalized * spinMagnitude, ForceMode.Impulse);
            }
        }

        Debug.Log($"[Cue Strike] Impulse={finalForceMagnitude:F2} | Angle={angleOffsetDegrees}° | Vertical={verticalAngleDegrees}° | Direction={forceDirection} | postVel={cueballRigidbody.velocity}", this);

        // clear requests/confirm after applying
        gameManager.ClearStrikeRequest();
        gameManager.ClearConfirmMode();
    }

    // Calculate force direction with angle offsets
    private Vector3 CalculateForceDirection(Vector3 baseDirection)
    {
        // Horizontal angle (English - left/right spin) — rotate around Y
        Vector3 horizontalRotated = Quaternion.AngleAxis(angleOffsetDegrees, Vector3.up) * baseDirection;

        // Vertical angle (Draw/follow — rotate around right axis)
        Vector3 rightAxis = Vector3.Cross(Vector3.up, horizontalRotated).normalized;
        Vector3 forceDir = Quaternion.AngleAxis(verticalAngleDegrees, rightAxis) * horizontalRotated;

        return forceDir.normalized;
    }
}