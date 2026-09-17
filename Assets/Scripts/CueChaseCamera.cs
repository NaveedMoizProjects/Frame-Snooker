using UnityEngine;
using Cinemachine;

// Drives CM_ThirdPersonCamera's transform directly from the cue's actual live aim direction
// (Cue.CurrentAimForward - the same direction the shot itself will travel), rather than through
// Cinemachine's Follow/LookAt indirection via the cue stick's child transforms. Those transforms
// turned out to be oriented for something else (their local axes don't point down the shot line -
// this whole camera was dormant/never tuned before, see project memory), so driving the position
// from the real aim direction is more reliable than fighting that rig's geometry.
//
// Requires the virtual camera's Body and Aim components set to "Do Nothing" in the inspector (no
// CinemachineTransposer/CinemachineComposer) so this script has sole control of the transform -
// Cinemachine still handles priority-based activation/blending between cameras, just not the
// positioning of this one.
//
// [ExecuteAlways]: without this, the saved transform is whatever LateUpdate last left it at when
// play mode was stopped - looks arbitrarily rotated/wrong in the Scene/Game view or on a fresh
// load until Play is pressed again. Running in edit mode too keeps it honestly pointed down the
// current aim line at all times.
[ExecuteAlways]
[RequireComponent(typeof(CinemachineVirtualCamera))]
public class CueChaseCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Cue cue;

    [Header("Framing - fine-tune these to taste")]
    [Tooltip("If on, the camera keeps repositioning every frame to track the cue ball and aim direction (the current behavior). If off, the camera stops moving entirely and just holds whatever pose it last had.")]
    [SerializeField] private bool followBall = true;
    [Tooltip("How far behind the cue ball the camera sits, opposite the aim direction (world units).")]
    [SerializeField] private float backDistance = 2.2f;
    [Tooltip("How high above the table felt the camera sits (world units) - roughly cue-stick height.")]
    [SerializeField] private float height = 1.6f;
    [Tooltip("How far down the aim line, past the cue ball, the camera looks at (world units). Bigger = flatter/less downward tilt, smaller = steeper.")]
    [SerializeField] private float lookAheadDistance = 6f;
    [Tooltip("Extra downward tilt on top of whatever looking at the ahead-point already gives (degrees).")]
    [SerializeField] private float extraPitchDegrees = 0f;
    [Tooltip("Smoothing time for position following the aim direction - 0 snaps instantly, higher values lag behind more.")]
    [SerializeField] private float smoothTime = 0.06f;
    [Tooltip("Max degrees/second the camera's look direction turns to catch up with the aim line. 0 snaps instantly (old behaviour) - a finite value makes the camera pan instead of jump-cutting whenever aim changes in a single frame (e.g. the AI setting its aim), same as it already eases position.")]
    [SerializeField] private float maxRotationDegreesPerSecond = 240f;

    private Rigidbody cueBall;
    private Vector3 posVelocity;

    void Start()
    {
        if (cue == null) cue = FindObjectOfType<Cue>();
        cueBall = FindCueBall();
    }

    void LateUpdate()
    {
        if (!followBall) return;

        if (cue == null || cueBall == null)
        {
            cueBall = FindCueBall();
            if (cue == null || cueBall == null) return;
        }

        Vector3 ballPos = cueBall.transform.position;
        Vector3 aimDir = cue.CurrentAimForward;
        if (aimDir.sqrMagnitude < 1e-6f) return;

        Vector3 desiredPos = ballPos - aimDir * backDistance + Vector3.up * height;
        Vector3 lookAt = ballPos + aimDir * lookAheadDistance;

        transform.position = smoothTime <= 0f
            ? desiredPos
            : Vector3.SmoothDamp(transform.position, desiredPos, ref posVelocity, smoothTime);

        Quaternion look = Quaternion.LookRotation((lookAt - transform.position).normalized, Vector3.up);
        if (extraPitchDegrees != 0f) look *= Quaternion.AngleAxis(extraPitchDegrees, Vector3.right);

        transform.rotation = maxRotationDegreesPerSecond <= 0f
            ? look
            : Quaternion.RotateTowards(transform.rotation, look, maxRotationDegreesPerSecond * Time.deltaTime);
    }

    // GameManager.Instance is a runtime-only singleton (set in its own Awake), so it's null in edit
    // mode - going straight to BallIdentity instead keeps this working with [ExecuteAlways] both in
    // and out of play mode, rather than silently doing nothing until Play is pressed.
    private static Rigidbody FindCueBall()
    {
        var identities = FindObjectsOfType<BallIdentity>();
        foreach (var id in identities)
        {
            if (id.Type == BallType.Cue) return id.GetComponent<Rigidbody>();
        }
        return null;
    }
}
