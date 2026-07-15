using System.Collections.Generic;
using UnityEngine;

public class Cue : MonoBehaviour
{
    // ---------------- Game refs ----------------
    [Header("Game Manager")]
    [SerializeField] protected GameManager gameManager;
    [SerializeField] protected GameObject Cueball;

    [Header("Cue Stick Refs")]
    [SerializeField] private GameObject CueStick;     // visual stick
    [SerializeField] private GameObject cuestickref;  // empty whose +Z points to cue ball

    // ---------------- Physics ----------------
    [Header("Cue Ball Rigidbody Settings")]
    [SerializeField] private float Mass = 1.0f;
    [SerializeField] private float Drag = 0.5f;
    [SerializeField] private float AngularDrag = 0.5f;

    // ---------------- Prediction lines ----------------
    [Header("Prediction Lines")]
    [SerializeField] private LineRenderer aimLineCue;     // cue-ball path (white)
    [SerializeField] private LineRenderer aimLineObject;  // object-ball path (red)
    [SerializeField] private int maxbounces = 1;       // reflections BEFORE any ball is hit
    [SerializeField] private float maxDistance = 60f;     // global safety cap

    [Tooltip("When nothing is hit, straight segment is clamped to this length.")]
    [SerializeField] private float maxNoHitLength = 8f;

    [Header("Contact Stub Settings (both red & white)")]
    [Tooltip("Length for BOTH post-collision stubs (red object line and small white cue line).")]
    [SerializeField] private float contactStubLength = 0.35f;
    [Tooltip("Trim a small gap before rail/pocket when drawing the stubs.")]
    [SerializeField] private float contactStubTrimGap = 0.05f;

    // ---------------- Layers ----------------
    [Header("Raycast Layers")]
    [SerializeField] private LayerMask ballLayer;    // all balls
    [SerializeField] private LayerMask tableLayer;   // cushions/borders/table (non-trigger)
    [SerializeField] private LayerMask pocketLayer;  // pocket mouths (TRIGGER colliders)
    [SerializeField] private bool stopAtPockets = true;

    [Tooltip("Read from SphereCollider if <= 0")]
    [SerializeField] private float cueBallRadius = -1f;

    [Header("Mobile Input")]
    [Tooltip("Multiplier applied to touch delta.x for cue rotation on mobile.")]
    [SerializeField] private float touchRotateMultiplier = 0.05f;

    [Header("Cue Stick Visual Alignment")]
    [Tooltip("Correction applied on top of the aim rotation. A default Unity Cylinder's length runs along its local Y axis, not Z, so it needs a 90 on X to lie flat and point at the ball instead of standing straight up. If your stick model already points down its own Z axis, set this to (0,0,0).")]
    [SerializeField] private Vector3 cueStickRotationOffset = new Vector3(90f, 0f, 0f);

    // ---------------- internals ----------------
    private bool isReadyToHit = false;
    private bool pendingStrike = false; // set in Update() on strike request, consumed in FixedUpdate()
    private Vector3 Cueoffset;
    private Rigidbody cueballRigidbody;

    // --------------- Unity ----------------
    void Start()
    {
        if (!ValidateReferences())
        {
            enabled = false; // fail loudly instead of throwing NullReferenceException mid-game
            return;
        }

        Cueoffset = CueStick.transform.position - Cueball.transform.position;

        cueballRigidbody = Cueball.GetComponent<Rigidbody>();
        if (!cueballRigidbody) cueballRigidbody = Cueball.AddComponent<Rigidbody>();
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
            else cueBallRadius = 0.0285f; // ~57mm dia / 2
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

    // ---------------- TEMP DIAGNOSTICS ----------------
    // Remove this whole block once the cue is confirmed moving correctly.
    [Header("TEMP DEBUG - delete after fixing")]
    [SerializeField] private bool debugLogging = true;
    private float debugLogTimer = 0f;

    void Update()
    {
        bool confirmed = gameManager.checkconfirmstrike();

        if (debugLogging)
        {
            debugLogTimer += Time.deltaTime;
            if (debugLogTimer > 0.5f) // log twice a second so Console doesn't flood
            {
                debugLogTimer = 0f;
                Debug.Log($"[CueDebug] confirmstrike={confirmed} | nextplay={gameManager.isNextPlay()} | MouseX raw={Input.GetAxis("Mouse X")} | CueStick pos={CueStick.transform.position} | Cueoffset={Cueoffset}");
            }
        }

        if (!confirmed)
        {
            float horizontalInput = Input.GetAxis("Mouse X") * 5f;

            // On mobile, prefer the first touch drag for horizontal rotation
            if (Application.isMobilePlatform && Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Moved)
                {
                    horizontalInput = t.deltaPosition.x * touchRotateMultiplier;
                }
            }

            if (Mathf.Abs(horizontalInput) > Mathf.Epsilon)
            {
                Quaternion rotation = Quaternion.AngleAxis(horizontalInput, Vector3.up);
                Cueoffset = rotation * Cueoffset;
            }
            if (gameManager.isNextPlay()) UpdateCue();
            LookAtBall();
        }

        if (isReadyToHit && !gameManager.checkconfirmstrike())
            GenerateAimPrediction();

        // Only the REQUEST is captured here. The actual physics force is applied in
        // FixedUpdate() below - keeps shot power/behavior identical regardless of frame rate
        // (dev doc Section 2: "Fixed timestep physics... never frame-dependent Update").
        if (gameManager.GetIsStrike() && isReadyToHit)
        {
            pendingStrike = true;
            isReadyToHit = false;
        }
        else
        {
            // Previously this line ran unconditionally every frame, which meant it could
            // immediately overwrite the "isReadyToHit = false" set above. Moved into the
            // else branch so a pending strike isn't clobbered in the same frame.
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

    // --------------- Prediction ----------------
    private static Vector3 Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up).normalized;

    private void GenerateAimPrediction()
    {
        if (!aimLineCue) return;
        if (aimLineObject) aimLineObject.positionCount = 0;

        float tableY = Cueball.transform.position.y;

        Vector3 origin = Cueball.transform.position + Vector3.up * 0.01f; // avoid self-hit
        Vector3 dir = Flat(Cueball.transform.position - cuestickref.transform.position);
        if (dir.sqrMagnitude < 1e-6f) return;

        List<Vector3> cuePts = new List<Vector3> { new Vector3(origin.x, tableY, origin.z) };

        float remain = maxDistance;
        int b = 0;

        while (b <= maxbounces && remain > 0f)
        {
            // rails + pockets (Collide with triggers to see pocket mouths)
            int railMask = tableLayer | pocketLayer;
            RaycastHit railHit;
            bool hasRail = Physics.Raycast(origin, dir, out railHit, remain, railMask, QueryTriggerInteraction.Collide);
            float dRail = hasRail ? railHit.distance : float.PositiveInfinity;

            // balls only (SphereCast for correct contact normal)
            RaycastHit ballHit;
            bool hasBall = Physics.SphereCast(origin, cueBallRadius, dir, out ballHit, remain, ballLayer, QueryTriggerInteraction.Ignore);
            float dBall = hasBall ? ballHit.distance : float.PositiveInfinity;

            if (dBall < dRail) // --- BALL contact: split paths ---
            {
                Vector3 contact = origin + dir * dBall; contact.y = tableY;
                cuePts.Add(contact);

                // object direction = -normal (line of centers)
                Vector3 objDir = Flat(-ballHit.normal);

                // cue direction AFTER collision (perpendicular component to objDir)
                Vector3 cueDirAfter = Flat(dir - Vector3.Project(dir, objDir));

                // ---- draw BOTH stubs with SAME length ----
                float len = Mathf.Max(0.01f, contactStubLength);

                // trim vs rail/pocket for object stub
                len = TrimLengthAgainstRail(contact, objDir, len, railMask);

                // 1) RED object-ball stub
                if (aimLineObject)
                {
                    Vector3 a = contact; a.y = tableY;
                    Vector3 bPt = a + objDir * len; bPt.y = tableY;
                    aimLineObject.positionCount = 2;
                    aimLineObject.SetPosition(0, a);
                    aimLineObject.SetPosition(1, bPt);
                }

                // 2) WHITE cue post-collision stub (same len)
                if (cueDirAfter.sqrMagnitude > 1e-6f)
                {
                    float lenCue = TrimLengthAgainstRail(contact, cueDirAfter, len, railMask);
                    Vector3 end = contact + cueDirAfter * lenCue; end.y = tableY;
                    cuePts.Add(end);
                }

                break; // stop at first object ball
            }
            else if (hasRail) // --- RAIL or POCKET ---
            {
                bool isPocket = ((1 << railHit.collider.gameObject.layer) & pocketLayer) != 0;

                Vector3 rp = railHit.point; rp.y = tableY;
                cuePts.Add(rp);
                remain -= railHit.distance;

                if (stopAtPockets && isPocket)
                    break; // stop at pocket mouth; no reflect

                // reflect on cushion
                Vector3 refl = Vector3.Reflect(dir, Flat(railHit.normal));
                origin = railHit.point + refl * 0.002f; origin.y = tableY;
                dir = Flat(refl);
                b++;
            }
            else // --- nothing hit: clamp straight length ---
            {
                Vector3 end = origin + dir * Mathf.Min(remain, maxNoHitLength);
                end.y = tableY;
                cuePts.Add(end);
                break;
            }
        }

        aimLineCue.positionCount = cuePts.Count;
        aimLineCue.SetPositions(cuePts.ToArray());
    }

    // trim helper: if rail/pocket is closer than desired length, reduce and leave a small gap
    private float TrimLengthAgainstRail(Vector3 start, Vector3 dir, float desiredLen, int railMask)
    {
        RaycastHit hit;
        if (Physics.Raycast(start + Vector3.up * 0.01f, dir, out hit, desiredLen, railMask, QueryTriggerInteraction.Collide))
        {
            return Mathf.Max(0.01f, hit.distance - Mathf.Max(0f, contactStubTrimGap));
        }
        return desiredLen;
    }

    // --------------- Aim + Strike ----------------
    private void LookAtBall()
    {
        Vector3 direction = Cueball.transform.position - CueStick.transform.position;
        if (direction.sqrMagnitude < 1e-6f) return;
        // Base look rotation aims local +Z at the ball; the offset then corrects for
        // whatever axis the actual mesh's length runs along (see tooltip above).
        CueStick.transform.rotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(cueStickRotationOffset);
    }

    private void ApplyForceToCueBall()
    {
        RaycastHit hit;
        if (Physics.Raycast(cuestickref.transform.position, cuestickref.transform.forward, out hit, 5f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.gameObject == Cueball)
            {
                Vector3 forceDirection = (Cueball.transform.position - cuestickref.transform.position).normalized;
                cueballRigidbody.AddForce(forceDirection * gameManager.GetStrikeForce(), ForceMode.Force);
            }
        }
        gameManager.SetIsStrike(false);
        gameManager.SetConfirmStrike(false);
    }

    private void UpdateCue()
    {
        CueStick.transform.position = Cueball.transform.position + Cueoffset;
        isReadyToHit = true;
    }

    // NOTE: the original WaitAndDo() coroutine and its "waittime" field were dead code -
    // never called anywhere - so they've been removed. Re-add if you have a planned use
    // for a post-strike delay (e.g. locking input briefly after a shot).
}