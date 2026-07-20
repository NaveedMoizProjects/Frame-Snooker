using UnityEngine;

// Attach this to each of the 6 pocket trigger colliders (the "capture radius" from the
// doc's Pocket data structure). No pocket-type/scoring logic here on purpose - this script
// only detects the event and hands the ball off to GameManager, which owns everything else.
[RequireComponent(typeof(Collider))]
public class PocketTrigger : MonoBehaviour
{
    void Reset()
    {
        // Convenience: pockets should always be triggers, never solid.
        var col = GetComponent<Collider>();
        if (col) col.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        if (rb == null) return;

        if (GameManager.Instance != null)
            GameManager.Instance.OnBallPotted(rb);
        else
            Debug.LogWarning("PocketTrigger: no GameManager.Instance found - ball entered pocket but nothing handled it.", this);
    }
}