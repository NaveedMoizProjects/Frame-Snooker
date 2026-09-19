using UnityEngine;
using UnityEngine.EventSystems;
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

    // A drag that starts on top of UI (the spin widget, the buttons) belongs to that UI, not to aiming.
    private bool dragStartedOverUI;
    private Renderer[] stickRenderers;
    private bool stickVisible = true;

    void Start()
    {
        mainCamera = Camera.main;

        if (!CueStick || !Cueball || !cuestickref)
        {
            Debug.LogError("CueVisualController: Missing references. Disable script until references are set.", this);
            enabled = false;
            return;
        }

        // Only renderers that start enabled, so hiding and re-showing can't switch on something
        // that was deliberately turned off.
        var all = CueStick.GetComponentsInChildren<Renderer>(true);
        var visible = new System.Collections.Generic.List<Renderer>();
        foreach (var r in all)
            if (r.enabled) visible.Add(r);
        stickRenderers = visible.ToArray();

        // Initialize distances/angle from current placement if available
        Vector3 local = CueStick.transform.position - Cueball.transform.position;
        initialHeight = local.y;
        if (initialHeight == 0f) initialHeight = heightOffset;
        horizontalDistance = new Vector2(local.x, local.z).magnitude;
        angleY = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
    }

    void Update()
    {
        // The stick is hidden while the cue ball is in hand, matching the reference placement screen.
        bool placing = GameManager.Instance != null && GameManager.Instance.IsAwaitingPlacement;
        SetStickVisible(!placing);

        // Uses the narrower IsAimFrozen (not IsInputLocked) so a pending foul decision doesn't freeze
        // the stick/camera along with it - the player still needs to look around the table to judge
        // the position before choosing Play or Play Again. Actually confirming or striking a shot is
        // blocked separately and unconditionally by GameManager itself during that decision.
        if (respectInputLock && GameManager.Instance != null && GameManager.Instance.IsAimFrozen)
            return;

        HandleInput();
        ApplyPositionAndRotation();
    }

    private void HandleInput()
    {
        // The AI drives angleY itself (via SetAzimuth) while it's animating its own aim - a human
        // dragging the mouse during that window would otherwise fight it, since confirmMode/
        // inputLocked don't engage until AFTER the AI's aim settles (same as a real player still
        // has free aim right up to pressing Confirm).
        if (GameManager.Instance != null && GameManager.Instance.IsAiTurn) return;

        // On Android (and other touch platforms) a single finger is also emulated as mouse button 0,
        // so both blocks below would otherwise fire for the same physical swipe and add up, doubling
        // the effective rotation. Touch takes priority whenever there's an active touch.
        bool touchActive = enableTouchControl && Input.touchCount > 0;

        // Mouse drag
        if (enableMouseControl && !touchActive)
        {
            if (Input.GetMouseButtonDown(0))
                dragStartedOverUI = IsPointerOverUI(-1);

            if (Input.GetMouseButton(0) && !dragStartedOverUI)
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
            if (t.phase == TouchPhase.Began)
                dragStartedOverUI = IsPointerOverUI(t.fingerId);

            if (t.phase == TouchPhase.Moved && !dragStartedOverUI)
            {
                float dx = t.deltaPosition.x / Mathf.Max(Screen.width, 1f); // normalized
                angleY += dx * rotationSensitivity * 0.25f; // scale (reduced for finer aim control)
            }
        }
    }

    private static bool IsPointerOverUI(int pointerId)
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId);
    }

    private void SetStickVisible(bool visible)
    {
        if (visible == stickVisible || stickRenderers == null) return;
        stickVisible = visible;
        foreach (var r in stickRenderers)
            if (r != null) r.enabled = visible;
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

    // Lets SnookerAI read where the stick currently is so it can animate from there to its target
    // angle instead of snapping - see SnookerAI.AnimateAimTo.
    public float CurrentAzimuth => angleY;

    // Optional helper: change horizontal distance (e.g. live tuning)
    public void SetHorizontalDistance(float distance)
    {
        horizontalDistance = Mathf.Max(0.01f, distance);
    }
}
