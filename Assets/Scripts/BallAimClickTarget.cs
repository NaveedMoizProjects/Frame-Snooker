using UnityEngine;

// Attach to every non-cue ball. Lets the player click any ball on their turn to redirect the aim
// stick straight at it, overriding whatever DefaultAimTargeting picked or the player was dragging
// towards. Mirrors ColourBallClickTarget's OnMouseDown pattern (Unity raycasts to any collider under
// the mouse automatically - no separate input system needed) but for aiming rather than nomination;
// the two coexist fine on a colour ball since they're independent components reacting to the same click.
[RequireComponent(typeof(BallIdentity))]
public class BallAimClickTarget : MonoBehaviour
{
    private BallIdentity identity;
    private CueVisualController cueVisual;

    void Awake()
    {
        identity = GetComponent<BallIdentity>();
    }

    void Start()
    {
        cueVisual = FindObjectOfType<CueVisualController>();
    }

    void OnMouseDown()
    {
        if (identity == null || identity.Type == BallType.Cue) return;

        var gm = GameManager.Instance;
        if (gm == null || cueVisual == null) return;
        // Same gating as the real strike calls (GameManager.BlockedAsHumanInputDuringAiTurn) - a
        // click during the AI's turn or while input is locked (confirm mode, placement, foul
        // decision) shouldn't be able to yank the stick around.
        if (gm.IsAiTurn || gm.IsInputLocked) return;

        Rigidbody cueBall = FindCueBall(gm);
        if (cueBall == null) return;

        AimUtility.PointAt(cueVisual, cueBall.transform.position, transform.position);

        // FREE_BALL.md: while a free ball is available, clicking any ball both aims at it AND
        // nominates it as this shot's ball-on - the existing ball-on selection pattern (aim = pick),
        // just no longer restricted to the normal legal set (GameManager.NominateFreeBall enforces
        // that; this just calls it opportunistically and lets it no-op if it isn't actually relevant).
        if (gm.IsFreeBallAvailable) gm.NominateFreeBall(identity.Type);
    }

    private static Rigidbody FindCueBall(GameManager gm)
    {
        foreach (var ball in gm.GetBalls())
        {
            if (ball == null) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id != null && id.Type == BallType.Cue) return ball;
        }
        return null;
    }
}
