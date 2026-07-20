using System.Collections.Generic;
using UnityEngine;

// Replaces reliance on Rigidbody.drag/angularDrag (which decays speed exponentially and
// technically never reaches exact zero) with a physically-grounded model:
//
//   1) SLIDING phase: right after a strike, the ball's surface velocity at the contact
//      point (linear + spin) doesn't match "rolling without slipping" yet. Kinetic
//      friction acts at that contact point until the mismatch disappears.
//   2) ROLLING phase: once linear motion and spin are in sync, only a small rolling
//      resistance remains, applied at the center of mass - this is what real cloth
//      friction feels like, and it's what makes long, gentle rolls look natural.
//   3) STOP: once speed drops below a small threshold, velocity is hard-zeroed - a real
//      stop, not an infinite crawl.
//
// Attach this once to the GameManager object (or any single object in the scene).
// It reads the same ball list GameManager already has - no need to wire balls twice.
public class BallRollingFriction : MonoBehaviour
{
    [Header("Friction Coefficients (tune by feel)")]
    [Tooltip("Kinetic friction while the ball is still sliding (contact-point speed above the slip threshold). Higher = skids die out faster.")]
    [SerializeField] private float slidingFriction = 0.2f;

    [Tooltip("Rolling resistance once the ball is rolling without slipping. Should be much smaller than slidingFriction - this is what lets long rolls happen.")]
    [SerializeField] private float rollingFriction = 0.015f;

    [Header("Thresholds")]
    [Tooltip("Contact-point slip speed below which the ball is considered to be rolling (not sliding) - in m/s equivalent.")]
    [SerializeField] private float slipSpeedThreshold = 0.05f;

    [Tooltip("Linear speed below which the ball is snapped to a full, exact stop.")]
    [SerializeField] private float stopSpeedThreshold = 0.03f;

    private List<Rigidbody> balls;
    private readonly Dictionary<Rigidbody, float> ballRadius = new Dictionary<Rigidbody, float>();

    void Start()
    {
        balls = (GameManager.Instance != null) ? GameManager.Instance.GetBalls() : null;
        if (balls == null)
        {
            Debug.LogError("BallRollingFriction: could not get balls list from GameManager.Instance.", this);
            enabled = false;
            return;
        }

        foreach (var ball in balls)
        {
            if (ball == null) continue;
            var sc = ball.GetComponent<SphereCollider>();
            float radius = sc ? sc.radius * Mathf.Max(ball.transform.lossyScale.x, ball.transform.lossyScale.y, ball.transform.lossyScale.z)
                               : 0.0285f; // fallback ~57mm diameter ball
            ballRadius[ball] = radius;
        }
    }

    void FixedUpdate()
    {
        if (balls == null) return;

        float g = Physics.gravity.magnitude;

        foreach (var ball in balls)
        {
            if (ball == null) continue;

            Vector3 v = ball.velocity;
            float speed = v.magnitude;
            if (speed < 0.0001f) continue; // already fully at rest, nothing to do

            float radius = ballRadius.TryGetValue(ball, out var r) ? r : 0.0285f;
            Vector3 omega = ball.angularVelocity;

            // Velocity of the point on the ball currently touching the table.
            Vector3 contactOffset = Vector3.down * radius;
            Vector3 contactVel = v + Vector3.Cross(omega, contactOffset);
            contactVel.y = 0f; // table is flat - only horizontal slip matters
            float slip = contactVel.magnitude;

            float m = ball.mass;

            if (slip > slipSpeedThreshold)
            {
                // Sliding: friction acts AT the contact point, opposing the slip direction.
                // Applying it off-center (via AddForceAtPosition) automatically generates the
                // correct torque too, so the ball naturally spins up into rolling motion.
                Vector3 worldContactPoint = ball.worldCenterOfMass + contactOffset;
                Vector3 frictionForce = -contactVel.normalized * slidingFriction * m * g;
                ball.AddForceAtPosition(frictionForce, worldContactPoint, ForceMode.Force);
            }
            else if (speed > stopSpeedThreshold)
            {
                // Pure rolling: small resistance opposing straight-line motion, applied at
                // the center so it doesn't disturb the spin.
                Vector3 rollForce = -v.normalized * rollingFriction * m * g;
                ball.AddForce(rollForce, ForceMode.Force);
            }
            else
            {
                // Real stop - no asymptotic crawl left over.
                ball.velocity = Vector3.zero;
                ball.angularVelocity = Vector3.zero;
            }
        }
    }
}