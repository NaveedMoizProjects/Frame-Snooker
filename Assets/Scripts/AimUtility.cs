using UnityEngine;

// Shared azimuth math for pointing the cue stick at a world position, used by both
// BallAimClickTarget (click-to-aim) and DefaultAimTargeting (auto-aim at the nearest legal ball).
// Same formula SnookerAI.AzimuthFor uses internally, so a clicked ball and an AI-planned shot agree
// on what "aimed at X" means.
public static class AimUtility
{
    public static void PointAt(CueVisualController cueVisual, Vector3 fromCueBallPos, Vector3 targetPos)
    {
        Vector3 dir = targetPos - fromCueBallPos;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        float azimuth = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
        cueVisual.SetAzimuth(azimuth);
    }
}
