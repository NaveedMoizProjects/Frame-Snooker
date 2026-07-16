using UnityEngine;
//using static UnityEditor.Rendering.CoreEditorDrawer<TData>;

/// <summary>
/// Visual-only controller for the CueStick. Keeps the visual stick positioned around the cue ball,
/// updates the cuestickref used by physics, and accepts drag input (mouse/touch) to rotate horizontally.
/// It respects GameManager.IsInputLocked to disable input when confirm-mode locks input.
/// </summary>
public class CueVisualController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject CueStick;      // visual stick
    [SerializeField] private GameObject Cueball;       // cue ball
    [SerializeField] private Transform cuestickref;    // empty whose +Z points to cue ball (used by physics)

    [Header("Rotation / Position")]
    [Tooltip("Horizontal rotation sensitivity (degrees per normalized input).")]
    [SerializeField] private float rotationSensitivity = 180f;
    [Tooltip("Distance from cue ball in XZ plane.")]
    [SerializeField] private float horizontalDistance = 0.5f;
    [Tooltip("Vertical offset above cue ball (world units).")]
    [SerializeField] private float heightOffset = 0.05f;
    [SerializeField] private Vector3 cueStickRotationOffset = new Vector3(90f, 0f, 0f);
    [Tooltip("If true, the controller follows the ball position (so stick stays attached while ball moves).")]
    [SerializeField] private bool followBall = true;

    [Header("Input")]
    [SerializeField] private bool enableMouseControl = true;
    [SerializeField] private bool enableTouchControl = true;
    [Tooltip("When true, visual input is ignored while GameManager input is locked (confirm pressed).")]
    [SerializeField] private bool respectInputLock = true;

    private float angleY; // degrees, around Y axis (azimuth)
    private float initialHeight;
    private Camera mainCamera;

    void Start()
    {
        mainCamera = Camera.main;

        if (!CueStick || !Cueball || !cuestickref)
        {
            Debug.LogError("CueVisualController: Missing references. Disable script until references are set.", this);
            enabled = false;
            return;
        }

        // Initialize distances/angle from current placement if available
        Vector3 local = CueStick.transform.position - Cueball.transform.position;
        initialHeight = local.y;
        if (initialHeight == 0f) initialHeight = heightOffset;
        horizontalDistance = new Vector2(local.x, local.z).magnitude;
        angleY = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
    }

    void Update()
    {
        if (respectInputLock && GameManager.Instance != null && GameManager.Instance.IsInputLocked)
            return;

        HandleInput();
        ApplyPositionAndRotation();
    }

    private void HandleInput()
    {
        // Mouse drag
        if (enableMouseControl)
        {
            if (Input.GetMouseButton(0))
            {
                // Use mouse delta X (frame) to rotate horizontally
                float dx = Input.GetAxis("Mouse X");
                angleY += dx * rotationSensitivity * Time.deltaTime;
            }
            else
            {
                // optional: small smoothing could be applied
            }
        }

        // Touch input (single finger horizontal drag)
        if (enableTouchControl && Input.touchCount == 1)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Moved)
            {
                float dx = t.deltaPosition.x / Mathf.Max(Screen.width, 1f); // normalized
                angleY += dx * rotationSensitivity * 0.5f; // scale
            }
        }
    }

    private void ApplyPositionAndRotation()
    {
        // Compute new XZ position using angleY and horizontalDistance
        float rad = angleY * Mathf.Deg2Rad;
        float x = Mathf.Sin(rad) * horizontalDistance;
        float z = Mathf.Cos(rad) * horizontalDistance;
        Vector3 target = Cueball.transform.position + new Vector3(x, initialHeight, z);

        if (followBall)
            CueStick.transform.position = target;
        else
        {
            // Keep original world position but rotate around cueball
            CueStick.transform.position = target;
        }

        // Make stick look at ball
        Vector3 lookDir = (Cueball.transform.position - CueStick.transform.position);
        if (lookDir.sqrMagnitude > 1e-6f)
            CueStick.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up) * Quaternion.Euler(cueStickRotationOffset);

        // Update cuestickref to a point on the stick that physics will read from.
        // Place it slightly behind the stick tip so raycasts from it hit the ball.
        if (cuestickref != null)
        {
            // position the ref a small distance along the stick forward vector towards ball
            float refOffset = 0.1f;
            cuestickref.position = CueStick.transform.position + CueStick.transform.forward * refOffset;
            cuestickref.rotation = CueStick.transform.rotation;
        }
    }

    // Optional helper: set absolute azimuth (degrees)
    public void SetAzimuth(float degrees)
    {
        angleY = degrees;
    }

    // Optional helper: change horizontal distance (e.g. live tuning)
    public void SetHorizontalDistance(float distance)
    {
        horizontalDistance = Mathf.Max(0.01f, distance);
    }
}