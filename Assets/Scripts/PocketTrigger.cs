using System.Collections.Generic;
using UnityEngine;

// Attach this to each of the 6 pocket trigger colliders (the "capture radius" from the
// doc's Pocket data structure). No pocket-type/scoring logic here on purpose - this script
// only detects the event and hands the ball off to GameManager, which owns everything else.
[RequireComponent(typeof(Collider))]
public class PocketTrigger : MonoBehaviour
{
    // Shared by all 6 pockets: a shot can pot balls at more than one pocket, and whether the shot
    // was a foul is only known once every ball on the table has stopped (GameManager.EvaluateFoul,
    // which runs on OnAllBallsStopped) - well after any individual OnTriggerEnter here. So the
    // effect for each pot is picked and queued immediately (sound still plays immediately too - a
    // potted ball audibly drops regardless of legality) but only actually spawned once the shot
    // resolves clean; a foul just discards the queue.
    private struct PendingEffect { public GameObject prefab; public Vector3 position; public float fallbackLifetime; }
    private static readonly List<PendingEffect> pendingEffects = new List<PendingEffect>();
    private static bool subscribedToShotEnd = false;

    [Header("Sound")]
    [Tooltip("Played when a ball is potted in this pocket. Optional.")]
    [SerializeField] private AudioClip pottedSound;
    [Tooltip("Random pitch range applied each time, so repeated pots don't sound identical.")]
    [SerializeField] private Vector2 pitchRange = new Vector2(0.95f, 1.05f);
    [Tooltip("Optional: assign an AudioSource to route through. If left empty, one is added automatically on this GameObject.")]
    [SerializeField] private AudioSource audioSource;

    [Header("Visual Effect")]
    [Tooltip("Spawned at the ball's position once the shot resolves as a legal pot (e.g. particle burst prefabs) - suppressed entirely if the shot turns out to be a foul. One is picked at random each pot - never the same one twice in a row - so pots don't all look identical. Optional.")]
    [SerializeField] private GameObject[] potEffectPrefabs;
    [Tooltip("Lifetime (seconds) for the spawned effect if it has no ParticleSystem to time itself by. A ParticleSystem's own duration + start lifetime is used instead when present.")]
    [SerializeField] private float effectFallbackLifetime = 2f;

    private int lastEffectIndex = -1;

    void Reset()
    {
        // Convenience: pockets should always be triggers, never solid.
        var col = GetComponent<Collider>();
        if (col) col.isTrigger = true;
    }

    void Start()
    {
        // Only the first pocket actually subscribes - the static queue and handler are shared by
        // all 6, so subscribing from every instance would spawn each queued effect 6 times over.
        if (subscribedToShotEnd) return;
        if (GameManager.Instance == null) return;
        GameManager.Instance.OnAllBallsStopped += ResolvePendingEffects;
        subscribedToShotEnd = true;
    }

    // Runs after GameManager.EvaluateFoul (subscribed in GameManager.Awake, which always runs
    // before this Start), so LastShotWasFoul is already correct for the shot that just ended.
    private static void ResolvePendingEffects()
    {
        if (!GameManager.Instance.LastShotWasFoul)
        {
            foreach (var pending in pendingEffects)
            {
                if (pending.prefab == null) continue;
                var fx = Instantiate(pending.prefab, pending.position, Quaternion.identity);
                var ps = fx.GetComponentInChildren<ParticleSystem>();
                float lifetime = ps != null ? ps.main.duration + ps.main.startLifetime.constantMax : pending.fallbackLifetime;
                Destroy(fx, lifetime);
            }
        }
        pendingEffects.Clear();
    }

    void OnTriggerEnter(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        if (rb == null) return;

        // Only actual balls can be potted. Several cushion/collider objects in the scenes also carry
        // Rigidbodies, and without this they fall into the pockets at frame start and GameManager
        // deactivates them - taking chunks of the cushion with them, so balls then escape the table.
        if (rb.GetComponent<BallIdentity>() == null) return;

        if (pottedSound != null)
        {
            var src = GetAudioSource();
            src.pitch = Random.Range(pitchRange.x, pitchRange.y);
            src.PlayOneShot(pottedSound);
        }

        GameObject chosenEffect = PickEffectPrefab();
        if (chosenEffect != null)
        {
            pendingEffects.Add(new PendingEffect
            {
                prefab = chosenEffect,
                position = rb.position,
                fallbackLifetime = effectFallbackLifetime
            });
        }

        if (GameManager.Instance != null)
            GameManager.Instance.OnBallPotted(rb);
        else
            Debug.LogWarning("PocketTrigger: no GameManager.Instance found - ball entered pocket but nothing handled it.", this);
    }

    // Picks a random entry from potEffectPrefabs, excluding whichever one played last time (when
    // there's more than one to choose from) so two pots in a row never show the same effect.
    private GameObject PickEffectPrefab()
    {
        if (potEffectPrefabs == null || potEffectPrefabs.Length == 0) return null;
        if (potEffectPrefabs.Length == 1) { lastEffectIndex = 0; return potEffectPrefabs[0]; }

        int index;
        do { index = Random.Range(0, potEffectPrefabs.Length); }
        while (index == lastEffectIndex);

        lastEffectIndex = index;
        return potEffectPrefabs[index];
    }

    // Lazily resolves/creates the AudioSource instead of the static PlayClipAtPoint helper, since
    // PlayClipAtPoint has no way to set pitch, and pitch variation is what keeps repeated pots
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