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

    [Header("Sound")]
    [Tooltip("Played on a ball-ball collision at or above slowCollisionThreshold (a normal/hard hit). Optional.")]
    [SerializeField] private AudioClip ballHitSound;
    [Tooltip("Played instead of ballHitSound when the collision speed is below slowCollisionThreshold (a soft/slow tap). " +
             "Leave empty to just play ballHitSound (quieter, via minVolume/maxVolume) for slow hits too.")]
    [SerializeField] private AudioClip slowBallHitSound;
    [Tooltip("Relative collision speed (m/s) that decides which clip plays: below this uses slowBallHitSound, at/above uses ballHitSound.")]
    [SerializeField] private float slowCollisionThreshold = 2f;
    [Tooltip("Relative collision speed (m/s) at or below which the hit plays at minVolume.")]
    [SerializeField] private float minCollisionVelocity = 0.5f;
    [Tooltip("Relative collision speed (m/s) at or above which the hit plays at maxVolume.")]
    [SerializeField] private float maxCollisionVelocity = 6f;
    [Range(0f, 1f)]
    [Tooltip("Volume used at/below minCollisionVelocity - keep above 0 so soft taps are still faintly audible.")]
    [SerializeField] private float minVolume = 0.05f;
    [Range(0f, 1f)]
    [Tooltip("Volume used at/above maxCollisionVelocity.")]
    [SerializeField] private float maxVolume = 1f;
    [Tooltip("Random pitch range applied each hit, so repeated collisions don't sound identical.")]
    [SerializeField] private Vector2 pitchRange = new Vector2(0.95f, 1.05f);
    [Tooltip("Optional: assign an AudioSource to route through. If left empty, one is added automatically on this GameObject.")]
    [SerializeField] private AudioSource audioSource;

    public BallType Type => ballType;
    public Vector3 SpawnPosition { get; private set; }

    void Awake()
    {
        SpawnPosition = useManualSpawnPosition ? manualSpawnPosition : transform.position;
    }

    void OnCollisionEnter(Collision collision)
    {
        Rigidbody other = collision.rigidbody;
        if (other == null) return; // cushions/table without a Rigidbody, or none at all

        var otherBall = other.GetComponent<BallIdentity>();
        if (otherBall == null) return; // only ball-ball contact, not cushions

        // Both balls in the pair get OnCollisionEnter for the same collision - only the lower
        // instance ID actually plays it, so it isn't heard twice per hit.
        if (GetInstanceID() > otherBall.GetInstanceID()) return;

        float speed = collision.relativeVelocity.magnitude;

        // Slow vs fast is a distinct clip choice, not just a volume difference - a soft kiss and a
        // hard smash can be entirely different recordings (e.g. a dull tap vs a sharp crack).
        bool isSlow = speed < slowCollisionThreshold;
        AudioClip clip = (isSlow && slowBallHitSound != null) ? slowBallHitSound : ballHitSound;
        if (clip == null) return;

        float t = Mathf.Clamp01(Mathf.InverseLerp(minCollisionVelocity, maxCollisionVelocity, speed));
        float volume = Mathf.Lerp(minVolume, maxVolume, t);

        var src = GetAudioSource();
        src.pitch = Random.Range(pitchRange.x, pitchRange.y);
        src.PlayOneShot(clip, volume);
    }

    // Lazily resolves/creates the AudioSource instead of the static PlayClipAtPoint helper, since
    // PlayClipAtPoint has no way to set pitch, and pitch variation is what keeps repeated collisions
    // from sounding robotic.
    private AudioSource GetAudioSource()
    {
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
        }
        return audioSource;
    }
}