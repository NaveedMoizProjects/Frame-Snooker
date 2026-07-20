using UnityEngine;

// Ball types per doc section 3.2. Cue and Red carry no point value of their own
// (Red's point value comes from GameManager.BallValue, same as every colour).
public enum BallType { Cue, Red, Yellow, Green, Brown, Blue, Pink, Black }

// Attach this to every ball GameObject that's also in GameManager's "balls" list
// (all 22: cue + 15 reds + 6 colours). This is the missing per-ball metadata Phase 3
// needs - GameManager only ever saw a flat List<Rigidbody> before, with no idea which
// entry was a Red vs a Blue vs the Cue ball.
[DisallowMultipleComponent]
public class BallIdentity : MonoBehaviour
{
    [Tooltip("Reds: leave as Red. Colours: set to the matching colour. Cue ball: set to Cue.")]
    [SerializeField] private BallType ballType = BallType.Red;

    [Header("Respawn Spot (Phase 3, section 5.3)")]
    [Tooltip("Where this ball respots to after a legal colour pot while reds remain. " +
             "Leave OFF to auto-capture this ball's scene starting position as its spot " +
             "(fine for reds/cue too, even though they don't currently use it).")]
    [SerializeField] private bool useManualSpawnPosition = false;
    [SerializeField] private Vector3 manualSpawnPosition = Vector3.zero;

    public BallType Type => ballType;
    public Vector3 SpawnPosition { get; private set; }

    void Awake()
    {
        SpawnPosition = useManualSpawnPosition ? manualSpawnPosition : transform.position;
    }
}