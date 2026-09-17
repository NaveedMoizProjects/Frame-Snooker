using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Ball-in-hand input (BALL_PLACEMENT_D.md section 4). While GameManager is awaiting placement the
/// player drags the cue ball across the table; on release GameManager validates the spot (inside the
/// D, clear of every other ball) and the ball snaps back to the last valid spot if it was rejected.
/// A tick button on the placement panel then commits it via GameManager.ConfirmPlacement.
///
/// The ball is made kinematic with its collider off while dragged, so sweeping it across the table
/// can't shove the other balls around.
/// </summary>
public class CueBallPlacement : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [SerializeField] private Rigidbody cueBall;

    [Header("D outline")]
    [Tooltip("Drawn while placing. Points come from the D geometry GameManager derives from the ball spots.")]
    [SerializeField] private LineRenderer dOutline;
    [SerializeField] private int outlineSegments = 48;

    private Collider cueCollider;
    private Camera cam;
    private bool dragging;
    private bool outlineBuilt;

    void Start()
    {
        if (gameManager == null) gameManager = GameManager.Instance;
        if (gameManager == null || cueBall == null)
        {
            Debug.LogError("CueBallPlacement: GameManager or cue ball not assigned.", this);
            enabled = false;
            return;
        }
        cueCollider = cueBall.GetComponent<Collider>();
    }

    void Update()
    {
        bool placing = gameManager.IsAwaitingPlacement;

        if (dOutline != null)
        {
            if (placing && !outlineBuilt) BuildOutline();
            dOutline.enabled = placing;
        }

        if (!placing)
        {
            if (dragging) EndDrag();
            return;
        }

        if (!dragging && Input.GetMouseButtonDown(0) && !IsPointerOverUI())
            BeginDrag();

        if (!dragging) return;

        if (Input.GetMouseButton(0))
        {
            if (TryGetTablePoint(Input.mousePosition, out Vector3 point)) DragTo(point);
        }
        else
        {
            EndDrag();
        }
    }

    private void BeginDrag()
    {
        dragging = true;
        cueBall.isKinematic = true;
        if (cueCollider != null) cueCollider.enabled = false;
    }

    private void DragTo(Vector3 point)
    {
        point.y = cueBall.transform.position.y; // the ball stays on the cloth
        MoveBall(point);
    }

    private void EndDrag()
    {
        dragging = false;

        Vector3 drop = cueBall.transform.position;
        MoveBall(gameManager.TryPlaceCueBall(drop) ? drop : gameManager.LastValidPlacement);

        if (cueCollider != null) cueCollider.enabled = true;
        cueBall.isKinematic = false;
        cueBall.velocity = Vector3.zero;
        cueBall.angularVelocity = Vector3.zero;
    }

    // Project settings leave Physics.autoSyncTransforms off, so writing only transform.position moves
    // the visible ball while the physics body stays put. Placement looked correct on screen but the
    // body kept its old pose, and the moment placement ended and the body went dynamic again it
    // dragged the ball back out of the D - which read as "placement isn't constrained".
    private void MoveBall(Vector3 point)
    {
        cueBall.position = point;
        cueBall.transform.position = point;
    }

    // Where the pointer meets the plane the ball's centre travels on.
    private bool TryGetTablePoint(Vector3 screenPosition, out Vector3 point)
    {
        point = Vector3.zero;
        if (cam == null) cam = Camera.main;
        if (cam == null) return false;

        var ballPlane = new Plane(Vector3.up, new Vector3(0f, cueBall.transform.position.y, 0f));
        Ray ray = cam.ScreenPointToRay(screenPosition);
        if (!ballPlane.Raycast(ray, out float distance)) return false;

        point = ray.GetPoint(distance);
        return true;
    }

    private static bool IsPointerOverUI()
    {
        if (EventSystem.current == null) return false;
        return Input.touchCount > 0
            ? EventSystem.current.IsPointerOverGameObject(Input.GetTouch(0).fingerId)
            : EventSystem.current.IsPointerOverGameObject();
    }

    // The half-circle arc; the LineRenderer's loop closes it back along the baulk line.
    private void BuildOutline()
    {
        outlineBuilt = true;

        Vector3 centre = gameManager.DCenter;
        float radius = gameManager.DRadius;
        Vector3 open = gameManager.DOpenDirection;
        Vector3 alongBaulk = Vector3.Cross(Vector3.up, open);

        var sphere = cueCollider as SphereCollider;
        Vector3 scale = cueBall.transform.lossyScale;
        float ballRadius = sphere != null ? sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) : 0f;
        float clothY = cueBall.transform.position.y - ballRadius + 0.005f;

        dOutline.useWorldSpace = true;
        dOutline.loop = true;
        dOutline.positionCount = outlineSegments + 1;
        for (int i = 0; i <= outlineSegments; i++)
        {
            float angle = Mathf.PI * i / outlineSegments;
            Vector3 p = centre + (alongBaulk * Mathf.Cos(angle) + open * Mathf.Sin(angle)) * radius;
            p.y = clothY;
            dOutline.SetPosition(i, p);
        }
    }
}
