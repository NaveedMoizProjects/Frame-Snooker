using System.Collections.Generic;
using UnityEngine;

public class Cue : MonoBehaviour
{
    [Header("Beta Cue Hitting Logic")]
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
    [SerializeField] private int maxReflectionBounces = 3;      // max number of cushion bounces to predict
    [SerializeField] private float reflectionEpsilon = 0.02f;  // small
    [SerializeField] private GameObject ghostBallPrefab;
    private GameObject currentGhostBall;

    [Header("Contact Stub Settings (both red & white)")]
    [SerializeField] private float contactStubLength = 5f;

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
        currentGhostBall = Instantiate(ghostBallPrefab);
        currentGhostBall.SetActive(false);
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

        Vector3 currentOrigin = origin;
        Vector3 currentDir = dir;
        float remainingDistance = maxDistance;
        int bounces = 0;

        // helper to test if a RaycastHit is a pocket by layer
        bool IsPocket(RaycastHit h)
        {
            if (h.collider == null) return false;
            int hitLayerMask = 1 << h.collider.gameObject.layer;
            return (pocketLayer.value & hitLayerMask) != 0;
        }

        while (remainingDistance > 0f && bounces <= maxReflectionBounces)
        {
            // 1) check ball along this segment
            RaycastHit ballHit;
            bool hasBall = Physics.SphereCast(currentOrigin, cueBallRadius, currentDir, out ballHit, remainingDistance, ballLayer, QueryTriggerInteraction.Ignore);
            float dBall = hasBall ? ballHit.distance : float.PositiveInfinity;

            // 2) check rail/pocket along this segment
            RaycastHit railHit;
            bool hasRail = Physics.Raycast(currentOrigin, currentDir, out railHit, remainingDistance, railMask, QueryTriggerInteraction.Collide);
            float dRail = hasRail ? railHit.distance : float.PositiveInfinity;

            if (dBall < dRail)
            {
                // cue-ball will contact the object ball before any rail
                Vector3 contact = currentOrigin + currentDir * dBall;
                contact.y = tableY;
                aimPoints.Add(contact);
                if (currentGhostBall != null)
                {
                    currentGhostBall.SetActive(true);
                    // Formula: Origin + (Direction * Distance)
                    Vector3 ghostBallCenter = origin + (currentDir * ballHit.distance);
                    // Position at the exact point of impact
                    currentGhostBall.transform.position = ghostBallCenter;
                }
                // Changes Second line which is red
                if (aimLineObject)
                {
                    Vector3 objDir = Flat(-ballHit.normal);
                    float stubLen = Mathf.Max(0.01f, contactStubLength);

                    // default end point if no cushion between contact and stubLen
                    Vector3 defaultBPt = contact + objDir * stubLen;
                    defaultBPt.y = tableY;

                    // small offset to avoid immediate overlap with the ball collider
                    Vector3 stubOrigin = contact + objDir * 0.01f;
                    stubOrigin.y = tableY;

                    // check specifically for cushions (tableLayer)
                    RaycastHit stubHit;
                    bool hitCushion = Physics.Raycast(stubOrigin, objDir, out stubHit, stubLen, tableLayer, QueryTriggerInteraction.Collide);

                    Vector3 bPt = hitCushion ? new Vector3(stubHit.point.x, tableY, stubHit.point.z) : defaultBPt;

                    aimLineObject.positionCount = 2;
                    aimLineObject.SetPosition(0, contact);
                    aimLineObject.SetPosition(1, bPt);
                }

                // finished prediction
                break;
            }
            else if (hasRail)
            {
                Vector3 rp = railHit.point;
                rp.y = tableY;
                aimPoints.Add(rp);

                // If it's a pocket and we should stop at pockets, stop here.
                if (stopAtPockets && IsPocket(railHit))
                {
                    break;
                }

                // Compute reflection and continue
                Vector3 refl = Vector3.Reflect(currentDir, railHit.normal);
                refl = Flat(refl);
                if (refl == Vector3.zero)
                {
                    // can't continue predictably
                    break;
                }

                // Move origin slightly along reflection to avoid immediately hitting the same collider
                currentOrigin = railHit.point + refl * reflectionEpsilon;
                currentOrigin.y = origin.y; // keep same height for flattened prediction

                // reduce remaining distance by distance consumed
                remainingDistance -= (dRail + reflectionEpsilon);

                currentDir = refl;
                bounces++;
                continue;
            }
            else
            {
                // no hit within remaining distance -> draw until maxNoHitLength or remainingDistance
                // also clamp to maxDistance so the white line never exceeds that inspector value
                float len = Mathf.Min(maxNoHitLength, remainingDistance, maxDistance);

                if (currentGhostBall != null) currentGhostBall.SetActive(false);
                // keep previous behavior: if a contactStubLength is set, clamp to that too
                if (contactStubLength > 0f)
                    len = Mathf.Min(len, contactStubLength);

                // check for cushions between currentOrigin and intended end
                RaycastHit cushionHit;
                bool hitCushion = Physics.Raycast(currentOrigin, currentDir, out cushionHit, len, tableLayer, QueryTriggerInteraction.Collide);

                Vector3 end = hitCushion ? new Vector3(cushionHit.point.x, tableY, cushionHit.point.z)
                                         : currentOrigin + currentDir * len;
                end.y = tableY;
                aimPoints.Add(end);
                break;
            }
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
       // gameManager.ClearConfirmMode();
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