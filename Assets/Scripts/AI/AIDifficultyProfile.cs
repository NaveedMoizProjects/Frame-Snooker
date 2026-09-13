using UnityEngine;

public enum SpinUsage { None, Basic, Full }

// Data for the one shared algorithm in SnookerAI (AI_SHOT_SELECTION.md). Beginner/Medium/Pro are
// three assets of this type, not three code paths - see AI_DIFFICULTY_LEVELS.md.
[CreateAssetMenu(fileName = "AIDifficultyProfile", menuName = "Snooker/AI Difficulty Profile")]
public class AIDifficultyProfile : ScriptableObject
{
    [Header("Aim error (degrees per shot)")]
    [Tooltip("Aim error range used on easy pots (potScore >= Easy Pot Score). X = min, Y = max. " +
             "X must stay above 0 - the golden rule is that no level is ever mathematically perfect.")]
    public Vector2 aimErrorDegreesEasy = new Vector2(4f, 8f);

    [Tooltip("Aim error range used on hard pots (potScore <= Hard Pot Score). Levels that don't scale " +
             "error with difficulty just set this equal to the easy range.")]
    public Vector2 aimErrorDegreesHard = new Vector2(4f, 8f);

    [Tooltip("potScore at or above which the easy aim-error range applies.")]
    public float easyPotScore = 0.8f;

    [Tooltip("potScore at or below which the hard aim-error range applies.")]
    public float hardPotScore = 0.3f;

    [Tooltip("Extra aim error added per ball already potted this visit, applied even before the soft " +
             "cap and never capped. Pro only - a long break gets shakier the longer it runs.")]
    public float aimErrorDegreesPerBallPotted = 0f;

    [Header("Power error")]
    [Tooltip("Maximum power error as a percentage of the intended power, applied as +/-.")]
    public float powerErrorPercent = 25f;

    [Tooltip("Smallest power error as a fraction of the maximum above. Keeps power error off exactly " +
             "zero on every shot, same golden rule as the aim-error floor.")]
    [Range(0.01f, 1f)] public float powerErrorFloorFraction = 0.2f;

    [Header("Shot selection")]
    [Tooltip("Best candidate must score at least this to be attempted at all; below it the AI plays safe.")]
    [Range(0f, 1f)] public float minAcceptablePotScore = 0.6f;

    [Tooltip("Share of this level's own aim-error range (plus leftover throw scatter) a pot must " +
             "survive to count as 'makeable' (see IsMakeableAtThisSkill in AI_SHOT_SELECTION.md). " +
             "Lower = more forgiving gate = more pots attempted, at the cost of some that don't drop. " +
             "A more capable level (smaller real aim error) can afford to set this lower, since even a " +
             "loosely-gated pot mostly still goes in for it - measured: at 1.0 Medium attempted pots " +
             "that dropped 93-98% of the time but ended most visits on 'no makeable pot'; at 0.25 it " +
             "attempted far more (dropping 87-89%) and pots per visit roughly doubled.")]
    [Range(0.01f, 1f)] public float makeabilityErrorFraction = 0.25f;

    [Tooltip("Chance of playing safety instead of the available pot, rolled in [X, Y]. Zero means the " +
             "level never deliberately plays safe and never deliberately snookers.")]
    public Vector2 safetyProbability = Vector2.zero;

    [Tooltip("The safety roll only happens when the best candidate scores below this - safety is " +
             "situational, not a flat global chance.")]
    [Range(0f, 1f)] public float safetyRollPotScore = 0f;

    [Tooltip("Weight of where the cue ball ends up. Pot weight is whatever is left over (1 - this).")]
    [Range(0f, 1f)] public float positionWeight = 0f;

    [Tooltip("None = always dead centre. Basic = follow/stun/screw only. Full = adds side spin.")]
    public SpinUsage deliberateSpinUsage = SpinUsage.None;

    [Tooltip("How many of the best candidates get compared by final score. 1 = just takes the most " +
             "obvious ball, 0 = surveys every valid candidate.")]
    public int candidateSurveyCount = 1;

    [Header("Pressure / soft cap")]
    [Tooltip("Balls this level can pot in one visit before the pressure ramp starts biting. Rolled " +
             "in [X, Y] at the start of each visit.")]
    public Vector2Int softPotCap = new Vector2Int(2, 3);

    [Tooltip("Fraction added to both error terms per ball potted beyond the soft cap (1.5 = +150%). " +
             "This is a ramp, never a hard block - the AI is always allowed to keep shooting.")]
    public float pressureRamp = 1.5f;

    // A level that never rolls for safety never goes looking for a snooker either; its fallback when
    // no pot is worth attempting is just a soft centre-ball contact on the ball on.
    public bool PlaysDeliberateSafety => safetyProbability.y > 0f;

    public float PotWeight => 1f - positionWeight;

    // Pro's error shrinks on easy pots and grows on hard ones; the other levels set both ranges the
    // same, so this returns a constant range for them with no special-casing.
    public Vector2 AimErrorRangeFor(float potScore)
    {
        float t = Mathf.InverseLerp(easyPotScore, hardPotScore, potScore);
        return Vector2.Lerp(aimErrorDegreesEasy, aimErrorDegreesHard, Mathf.Clamp01(t));
    }

    public int RollSoftPotCap() => Random.Range(softPotCap.x, softPotCap.y + 1);

    public float RollSafetyProbability() => Random.Range(safetyProbability.x, safetyProbability.y);

    void OnValidate()
    {
        // The golden rule is structural, not a tuning choice: a zero floor here would let a level
        // produce a literally perfect shot, which every level is supposed to be incapable of.
        aimErrorDegreesEasy.x = Mathf.Max(0.01f, aimErrorDegreesEasy.x);
        aimErrorDegreesHard.x = Mathf.Max(0.01f, aimErrorDegreesHard.x);
        aimErrorDegreesEasy.y = Mathf.Max(aimErrorDegreesEasy.x, aimErrorDegreesEasy.y);
        aimErrorDegreesHard.y = Mathf.Max(aimErrorDegreesHard.x, aimErrorDegreesHard.y);
        powerErrorPercent = Mathf.Max(0.01f, powerErrorPercent);
    }
}
