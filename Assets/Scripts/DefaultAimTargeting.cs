using UnityEngine;

// When it becomes the player's turn - or the legal ball-on changes mid-visit (a red potted,
// colour nomination opens up) - points the cue stick at the nearest legal ball automatically.
// BallAimClickTarget lets the player override this by clicking any other ball; this script only
// ever sets a starting point, never fights a click after the fact.
//
// Legal-ball rule mirrors SnookerAI.CollectLegalTargets exactly (on=Red -> all reds, on=Colour with
// a specific colour -> just that colour, nomination pending -> any colour), so the ball this points
// at is the same one the AI would consider "the" ball on.
public class DefaultAimTargeting : MonoBehaviour
{
    [SerializeField] private CueVisualController cueVisual;

    // GameManager has no "placement confirmed" event, only the IsAwaitingPlacement flag - the cue
    // ball only really has a meaningful position once that flag drops, so this watches for the
    // false-drops-from-true edge instead of trying to aim mid-placement-drag.
    private bool wasAwaitingPlacement;

    void Start()
    {
        if (cueVisual == null) cueVisual = GetComponent<CueVisualController>();
        if (cueVisual == null) cueVisual = FindObjectOfType<CueVisualController>();

        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnTurnChanged += OnTurnOrTargetChanged;
        gm.OnTargetChanged += OnTargetChanged;
        wasAwaitingPlacement = gm.IsAwaitingPlacement;

        TryAimAtNearestLegalBall();
    }

    void OnDestroy()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        gm.OnTurnChanged -= OnTurnOrTargetChanged;
        gm.OnTargetChanged -= OnTargetChanged;
    }

    void Update()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        bool awaiting = gm.IsAwaitingPlacement;
        if (wasAwaitingPlacement && !awaiting) TryAimAtNearestLegalBall();
        wasAwaitingPlacement = awaiting;
    }

    private void OnTurnOrTargetChanged(int newPlayerIndex) => TryAimAtNearestLegalBall();
    private void OnTargetChanged(GameManager.TargetBallState state, BallType? colour) => TryAimAtNearestLegalBall();

    private void TryAimAtNearestLegalBall()
    {
        var gm = GameManager.Instance;
        if (gm == null || cueVisual == null || gm.IsAiTurn) return;

        Rigidbody cueBall = null;
        foreach (var ball in gm.GetBalls())
        {
            if (ball == null || !ball.gameObject.activeInHierarchy) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id != null && id.Type == BallType.Cue) { cueBall = ball; break; }
        }
        if (cueBall == null) return;

        Rigidbody nearest = null;
        float nearestSqDist = float.PositiveInfinity;
        foreach (var ball in gm.GetBalls())
        {
            if (ball == null || !ball.gameObject.activeInHierarchy || ball == cueBall) continue;
            var id = ball.GetComponent<BallIdentity>();
            if (id == null || !IsLegalTarget(gm, id.Type)) continue;

            float sq = (ball.transform.position - cueBall.transform.position).sqrMagnitude;
            if (sq < nearestSqDist) { nearestSqDist = sq; nearest = ball; }
        }
        if (nearest == null) return;

        AimUtility.PointAt(cueVisual, cueBall.transform.position, nearest.transform.position);
    }

    private static bool IsLegalTarget(GameManager gm, BallType type)
    {
        if (gm.CurrentTargetState == GameManager.TargetBallState.Red) return type == BallType.Red;
        if (gm.CurrentTargetColour.HasValue) return type == gm.CurrentTargetColour.Value;
        if (gm.NeedsColourNomination) return type != BallType.Red && type != BallType.Cue;
        return false;
    }
}
