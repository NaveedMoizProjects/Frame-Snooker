using UnityEngine;

// Attach this to the CUE BALL specifically (not other balls). Reports the first ball
// it physically collides with each shot to GameManager - this is the "firstBallContacted"
// input the doc's Foul Table (section 6.1/6.2/6.3) is built around.
//
// Requires a real (non-trigger) Collider + Rigidbody on the cue ball, which it already
// has for normal ball-ball physics - this just listens to the same collisions.
[RequireComponent(typeof(Rigidbody))]
public class CueBallContactTracker : MonoBehaviour
{
    void OnCollisionEnter(Collision collision)
    {
        Rigidbody other = collision.rigidbody;
        if (other == null) return; // hit a cushion/rail (no Rigidbody) - doesn't count as a "ball contact"

        if (GameManager.Instance != null)
            GameManager.Instance.ReportCueBallContact(other);
    }
}