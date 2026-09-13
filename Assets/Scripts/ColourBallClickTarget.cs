using UnityEngine;

// Attach to each of the six colour balls (Yellow/Green/Brown/Blue/Pink/Black) already on the
// table. Replaces the old picker-panel nomination flow (ColourNominationUI's ColourSlectionPanel,
// now kept permanently hidden) with clicking the actual ball you want on the table.
[RequireComponent(typeof(BallIdentity))]
public class ColourBallClickTarget : MonoBehaviour
{
    private BallIdentity identity;

    void Awake()
    {
        identity = GetComponent<BallIdentity>();
    }

    // Unity sends this to any GameObject with a Collider when the mouse is pressed over it, using
    // Camera.main and a physics raycast automatically - no separate input/raycast system needed.
    // GameManager.OnColourNominated already validates whether a nomination is legal right now
    // (Colour state, reds remaining, not the AI's turn), so a click at the wrong moment is simply
    // ignored there rather than needing to be gated here too.
    void OnMouseDown()
    {
        if (identity == null || GameManager.Instance == null) return;
        GameManager.Instance.OnColourNominated(identity.Type);
    }
}
