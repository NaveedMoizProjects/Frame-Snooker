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

    // ---------------- Spin (SPIN_LOGIC.md) ----------------
    [Header("Spin")]
    [Tooltip("How far off-centre the tip meets the ball at full dot deflection, as a fraction of the ball radius.")]
    [Range(0f, 1f)]
    [SerializeField] private float spinContactOffset = 0.7f;
    [Tooltip("Cue elevation above horizontal. A level cue (0) can't make side spin curve the ball - " +
             "the tilt is what turns side spin into swerve on the cloth.")]
    [Range(0f, 30f)]
    [SerializeField] private float cueElevationDegrees = 10f;

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
    private readonly RaycastHit[] castBuffer = new RaycastHit[16];

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
                Debug.Log($"[CueDebug] confirmMode={confirmed} | nextplay={gameManager.isNextPlay()} | strikeForce={gameManager.GetStrikeForce()} | forceMul={forceMultiplier} | spin={gameManager.SpinOffset}");
            }
        }

        // No aim line while the cue ball is in hand - the player is positioning it, not aiming.
        if (gameManager.IsAwaitingPlacement)
            HidePrediction();
        else if (isReadyToHit && !confirmed)
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

    // Where the cue is pointing right now, derived purely from the stick's own position. Aim comes
    // from nowhere else - SnookerAI steers this by moving the stick, exactly like a human drag does,
    // so there is no second aiming path that could be more accurate than what the player gets.
    public Vector3 CurrentAimForward => Flat(Cueball.transform.position - cuestickref.transform.position);

    public float CueBallRadius => cueBallRadius;

    // Direction the cue ball deflects to after contact: the component of its approach perpendicular
    // to the object ball's departure line (equal-mass elastic "throw-off"). Shared with SnookerAI's
    // position approximation so both use one definition of where the cue ball goes next.
    public Vector3 CueDirectionAfterContact(Vector3 approachDir, Vector3 objectBallDir)
        => Flat(approachDir - Vector3.Project(approachDir, objectBallDir));

    // The line-of-sight test behind GenerateAimPrediction's ball/rail casts, exposed so shot
    // selection asks the same question the aim line answers: is anything between these two points?
    // ignoreA/ignoreB drop the balls the caller is reasoning about (the target it wants to hit, and
    // the cue ball when it is being planned into a position it isn't standing in yet).
    public bool IsPathClear(Vector3 from, Vector3 to, Rigidbody ignoreA, Rigidbody ignoreB, bool checkCushions)
    {
        Vector3 delta = to - from;
        delta.y = 0f;
        float distance = delta.magnitude;
        if (distance <= 1e-4f) return true;
        Vector3 dir = delta / distance;

        // Start clear of whatever sits at 'from', otherwise the cast begins inside its own collider.
        float skip = cueBallRadius + 1e-3f;
        if (distance <= skip) return true;
        Vector3 origin = from + dir * skip;
        float span = distance - skip;

        int count = Physics.SphereCastNonAlloc(origin, cueBallRadius, dir, castBuffer, span, ballLayer, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Rigidbody rb = castBuffer[i].collider.attachedRigidbody;
            if (rb == null || rb == ignoreA || rb == ignoreB) continue;
            return false;
        }

        return !checkCushions || !Physics.Raycast(origin, dir, span, tableLayer, QueryTriggerInteraction.Ignore);
    }

    // Stops a predicted roll at the first cushion it would meet, so position guesses can't score a
    // resting spot that is actually off the table.
    public Vector3 ClampToCushion(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        delta.y = 0f;
        float distance = delta.magnitude;
        if (distance <= 1e-4f) return to;
        Vector3 dir = delta / distance;

        return Physics.Raycast(from, dir, out RaycastHit hit, distance, tableLayer, QueryTriggerInteraction.Ignore)
            ? new Vector3(hit.point.x, to.y, hit.point.z) - dir * cueBallRadius
            : to;
    }

    private void HidePrediction()
    {
        if (aimLineCue) aimLineCue.positionCount = 0;
        if (aimLineObject) aimLineObject.positionCount = 0;
        if (currentGhostBall != null) currentGhostBall.SetActive(false);
    }

    // simplified, readable prediction: single straight segment
    private void GenerateAimPrediction()
    {
        if (aimLineCue == null) return;
        if (aimLineObject) aimLineObject.positionCount = 0;

        float tableY = Cueball.transform.position.y;
        Vector3 origin = Cueball.transform.position + Vector3.up * 0.01f;
        // Pure aim: spin only moves where the tip meets the ball, never the launch direction.
        Vector3 dir = CurrentAimForward;
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

                // Direction the OBJECT ball will travel: along the line connecting the two
                // ball centers at contact (computed once here so both the red object-line and
                // the white cue-deflection stub below can use the same value).
                Vector3 objDir = Flat(-ballHit.normal);

                // Changes Second line which is red
                if (aimLineObject)
                {
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

                // NEW: direction the CUE ball deflects to after contact (perpendicular to the
                // object-ball direction, standard equal-mass elastic collision "throw-off" line).
                // This is what was missing - previously aimPoints stopped exactly at contact,
                // so the white line never showed where the cue ball goes after the hit.
                Vector3 cueDirAfter = CueDirectionAfterContact(currentDir, objDir);
                if (cueDirAfter != Vector3.zero)
                {
                    float cueStubLen = Mathf.Max(0.01f, contactStubLength);

                    Vector3 cueStubOrigin = contact + cueDirAfter * 0.01f;
                    cueStubOrigin.y = tableY;

                    RaycastHit cueStubHit;
                    bool cueHitCushion = Physics.Raycast(cueStubOrigin, cueDirAfter, out cueStubHit, cueStubLen, tableLayer, QueryTriggerInteraction.Collide);

                    Vector3 cueEnd = cueHitCushion
                        ? new Vector3(cueStubHit.point.x, tableY, cueStubHit.point.z)
                        : contact + cueDirAfter * cueStubLen;
                    cueEnd.y = tableY;

                    aimPoints.Add(cueEnd);
                }
                // NOTE: for a near dead-center hit, objDir ends up almost parallel to currentDir,
                // so cueDirAfter naturally comes out very short/zero - that's correct "stun shot"
                // physics (cue ball stops), not a bug. The stub will just be tiny/invisible then.

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

        Vector3 aimForward = CurrentAimForward;
        if (aimForward == Vector3.zero)
        {
            Debug.LogWarning("Cue too close to ball to apply force!");
            gameManager.ClearStrikeRequest();
            gameManager.ClearConfirmMode();
            return;
        }

        // The cue strikes along the pure aim, tilted down by its elevation. Spin never changes this
        // direction - it only moves where the tip meets the ball, and AddForceAtPosition turns that
        // offset into the matching spin (tau = r x F). Follow, screw, stun and swerve all come out of
        // this one impulse; there's deliberately no per-dot-position special casing anywhere.
        Vector3 right = Vector3.Cross(Vector3.up, aimForward).normalized;
        float elevation = cueElevationDegrees * Mathf.Deg2Rad;
        Vector3 strikeDirection = aimForward * Mathf.Cos(elevation) - Vector3.up * Mathf.Sin(elevation);

        Vector2 spin = gameManager.StrikeSpin;
        float maxOffset = cueBallRadius * spinContactOffset;
        Vector3 contactPoint = cueballRigidbody.worldCenterOfMass - strikeDirection * cueBallRadius
                             + right * (spin.x * maxOffset)
                             + Vector3.up * (spin.y * maxOffset);

        float impulse = gameManager.GetStrikeForce() * forceMultiplier;

        // Ensure cue ball is dynamic
        if (cueballRigidbody.isKinematic)
        {
            Debug.LogWarning("Cueball Rigidbody is kinematic. Setting isKinematic = false so physics can move it.", this);
            cueballRigidbody.isKinematic = false;
        }

        cueballRigidbody.WakeUp();
        cueballRigidbody.AddForceAtPosition(strikeDirection * impulse, contactPoint, ForceMode.Impulse);

        Debug.Log($"[Cue Strike] Impulse={impulse:F2} | Spin={spin} | Elevation={cueElevationDegrees}deg | Aim={aimForward} | mass={cueballRigidbody.mass}", this);

        // clear requests/confirm after applying
        gameManager.ClearStrikeRequest();
        // gameManager.ClearConfirmMode();
    }
}
