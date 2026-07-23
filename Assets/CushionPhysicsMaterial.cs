using UnityEngine;

/// <summary>
/// Minimal cushion: on collision with objects in the Ball layer, increase only the horizontal (X/Z)
/// components of the ball's velocity. Does not alter Y so you can lock Y on the ball Rigidbody.
/// Attach to a cushion GameObject with a non-trigger Collider.
/// </summary>
public class CushionPhysicsMaterial : MonoBehaviour
{
    [Header("Ball detection")]
    [Tooltip("Which layers are considered balls (select the 'Ball' layer).")]
    [SerializeField] private LayerMask ballLayer = 0;

    [Header("Horizontal speed boost")]
    [Tooltip("Multiplier applied to the horizontal (XZ) velocity. 1 = no change, >1 = faster.")]
    [SerializeField, Range(1f, 5f)] private float speedMultiplier = 1.2f;

    [Tooltip("If the resulting horizontal speed is below this, boost it up to this magnitude (0 = disabled).")]
    [SerializeField, Min(0f)] private float minHorizontalSpeed = 0f;

    private void OnCollisionEnter(Collision collision)
    {
        // Only affect configured ball layers
        if ((ballLayer.value & (1 << collision.gameObject.layer)) == 0)
            return;

        Rigidbody rb = collision.rigidbody;
        if (rb == null)
            return;

        Vector3 v = rb.velocity;

        // Extract horizontal velocity (XZ) and keep Y as-is
        Vector3 horizontal = new Vector3(v.x, 0f, v.z);

        // If there's no horizontal direction, try to infer one from contact tangent
        if (horizontal.sqrMagnitude == 0f)
        {
            // Use the averaged contact tangent as a fallback direction
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < collision.contactCount; i++)
                normal += collision.GetContact(i).normal;
            normal = normal.normalized;
            // tangent along surface (parallel to world XZ)
            Vector3 tangent = Vector3.Cross(normal, Vector3.up).normalized;
            if (tangent == Vector3.zero)
                tangent = Vector3.right;
            horizontal = tangent * 0.01f; // tiny seed so direction exists
        }

        // Scale horizontal velocity
        Vector3 newHorizontal = horizontal * speedMultiplier;

        // Enforce minimum horizontal speed if requested
        if (minHorizontalSpeed > 0f && newHorizontal.sqrMagnitude < minHorizontalSpeed * minHorizontalSpeed)
        {
            newHorizontal = newHorizontal.normalized * minHorizontalSpeed;
        }

        // Apply new velocity preserving original Y
        rb.velocity = new Vector3(newHorizontal.x, v.y, newHorizontal.z);
    }
}