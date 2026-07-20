using UnityEngine;
using Cinemachine;

public class CameraSwitching : MonoBehaviour
{
    private enum CameraMode { TopDown, ThirdPerson, FirstPerson }

    [Header("Camera")]
    [SerializeField] protected CinemachineVirtualCamera CM_TopDownCamera;
    [SerializeField] protected CinemachineVirtualCamera CM_ThirdPersonCamera;
    [SerializeField] protected CinemachineFreeLook CM_FirstPersonCamera;

    [Header("Priorities")]
    [SerializeField] private int inactivePriority = 10;
    [SerializeField] private int activePriority = 20;

    [Header("Mobile Touch")]
    [Tooltip("Sensitivity applied to touch delta when controlling the FreeLook camera on mobile.")]
    [SerializeField] private float freeLookTouchSensitivity = 0.02f;

    // Tracked explicitly instead of inferred by comparing priorities each frame -
    // cheaper, and can't get confused if priorities are ever tied or changed elsewhere.
    private CameraMode activeMode = CameraMode.TopDown;

    private void Awake()
    {
        if (!CM_TopDownCamera) Debug.LogWarning("CameraSwitching: CM_TopDownCamera not assigned.", this);
        if (!CM_ThirdPersonCamera) Debug.LogWarning("CameraSwitching: CM_ThirdPersonCamera not assigned.", this);
        if (!CM_FirstPersonCamera) Debug.LogWarning("CameraSwitching: CM_FirstPersonCamera not assigned.", this);
    }

    private void Start()
    {
        // Activates the Top Down camera by default
        SwitchToTopDownCamera();
    }

    private void Update()
    {
        // Keyboard shortcuts - useful for quick testing in editor/PC, harmlessly unused on mobile builds
        if (Input.GetKeyDown(KeyCode.Alpha1)) SwitchToTopDownCamera();
        if (Input.GetKeyDown(KeyCode.Alpha2)) SwitchToThirdPersonCamera();
        if (Input.GetKeyDown(KeyCode.Alpha3)) SwitchToFirstPersonCamera();

        // Mobile: when FreeLook is active, drive its axes from the first touch delta
        if (Application.isMobilePlatform
            && activeMode == CameraMode.FirstPerson
            && CM_FirstPersonCamera != null
            && Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            if (t.phase == TouchPhase.Moved)
            {
                float dx = t.deltaPosition.x * freeLookTouchSensitivity;
                float dy = t.deltaPosition.y * freeLookTouchSensitivity;

                CM_FirstPersonCamera.m_XAxis.Value += dx;
                CM_FirstPersonCamera.m_YAxis.Value -= dy; // invert Y so drag up => look up
            }
        }
    }

    public void SwitchToTopDownCamera() => Activate(CameraMode.TopDown);
    public void SwitchToThirdPersonCamera() => Activate(CameraMode.ThirdPerson);
    public void SwitchToFirstPersonCamera() => Activate(CameraMode.FirstPerson);

    // Single shared implementation instead of two near-identical overloads.
    private void Activate(CameraMode mode)
    {
        if (CM_TopDownCamera)
            CM_TopDownCamera.Priority = (mode == CameraMode.TopDown) ? activePriority : inactivePriority;

        if (CM_ThirdPersonCamera)
            CM_ThirdPersonCamera.Priority = (mode == CameraMode.ThirdPerson) ? activePriority : inactivePriority;

        if (CM_FirstPersonCamera)
            CM_FirstPersonCamera.Priority = (mode == CameraMode.FirstPerson) ? activePriority : inactivePriority;

        activeMode = mode;
    }
}