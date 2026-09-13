// TEMPORARY test harness for measuring SnookerAI misses near a red cluster. Not part of the game - delete
// before committing. Player 0 is a stand-in human that deliberately plays a minimum-power shot into open
// space (a miss) to hand the table back; the scene's SnookerAI plays its normal visit.
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class TempAIVisitHarness
{
    private static EditorApplication.CallbackFunction update;
    private static Application.LogCallback logCallback;
    public static string status = "";

    public static string Run(string outFile, int seed, int maxVisits)
    {
        if (!Application.isPlaying) return "not playing";
        Stop();

        var gm = GameManager.Instance;
        var ai = Object.FindObjectOfType<SnookerAI>();
        var so = new SerializedObject(ai);
        var profile = (AIDifficultyProfile)so.FindProperty("profile").objectReferenceValue;
        int aiIdx = so.FindProperty("aiPlayerIndex").intValue;
        so.FindProperty("thinkingDelaySeconds").vector2Value = new Vector2(0.05f, 0.1f);
        so.FindProperty("debugLogging").boolValue = true;
        so.ApplyModifiedPropertiesWithoutUndo();
        float margin = so.FindProperty("sightMarginBallRadii").floatValue;
        ai.enabled = true;

        var np = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(GameManager).GetField("debugLogging", np).SetValue(gm, false);
        var foulField = typeof(GameManager).GetField("lastShotWasFoul", np);
        var cue = Object.FindObjectOfType<Cue>();
        typeof(Cue).GetField("debugLogging", np).SetValue(cue, false);
        var cv = Object.FindObjectOfType<CueVisualController>();
        var slider = Object.FindObjectOfType<ShotPowerSlider>();

        Rigidbody cueBall = null;
        foreach (var b in gm.GetBalls()) if (b.GetComponent<BallIdentity>().Type == BallType.Cue) cueBall = b;
        float by = cueBall.position.y;
        float diameter = cue.CueBallRadius * 2f;
        var rng = new System.Random(seed);

        // Rack positions, read once from the reds' spawn spots.
        var rack = new List<Vector3>();
        foreach (var b in gm.GetBalls()) { var id = b.GetComponent<BallIdentity>(); if (id.Type == BallType.Red) rack.Add(id.SpawnPosition); }

        System.Action layout = () =>
        {
            var placed = new List<Vector3>();
            foreach (var b in gm.GetBalls())
            {
                var id = b.GetComponent<BallIdentity>();
                if (id.Type == BallType.Red || id.Type == BallType.Cue) continue;
                if (!b.gameObject.activeInHierarchy) b.gameObject.SetActive(true);
                b.velocity = Vector3.zero; b.angularVelocity = Vector3.zero;
                b.position = id.SpawnPosition; b.transform.position = id.SpawnPosition; placed.Add(id.SpawnPosition);
            }
            var reds = new List<Rigidbody>();
            foreach (var b in gm.GetBalls()) if (b.GetComponent<BallIdentity>().Type == BallType.Red) reds.Add(b);
            int total = 8 + rng.Next(8);            // 8..15 reds on
            int clustered = 5 + rng.Next(8);        // 5..12 of them still in the pack
            if (clustered > total) clustered = total;
            var slots = new List<int>(); for (int i = 0; i < rack.Count; i++) slots.Add(i);
            for (int i = 0; i < slots.Count; i++) { int j = rng.Next(slots.Count); int t = slots[i]; slots[i] = slots[j]; slots[j] = t; }
            for (int r = 0; r < reds.Count; r++)
            {
                var b = reds[r];
                if (r >= total) { b.gameObject.SetActive(false); continue; }
                if (!b.gameObject.activeInHierarchy) b.gameObject.SetActive(true);
                b.velocity = Vector3.zero; b.angularVelocity = Vector3.zero;
                Vector3 p = Vector3.zero; bool ok = false;
                for (int tries = 0; tries < 500 && !ok; tries++)
                {
                    if (r < clustered)
                    {
                        // A loosened pack: rack spot pushed out by up to 0.35 ball widths.
                        Vector3 s = rack[slots[(r + tries) % slots.Count]];
                        p = s + new Vector3((float)(rng.NextDouble() * 2 - 1), 0, (float)(rng.NextDouble() * 2 - 1)) * diameter * 0.35f;
                    }
                    else p = new Vector3((float)(rng.NextDouble() * 9.4 - 4.7), by, (float)(rng.NextDouble() * 14.0 - 7.0));
                    p.y = by;
                    ok = true;
                    float need = r < clustered ? diameter + 0.01f : 0.6f;
                    foreach (var q in placed) if ((q - p).magnitude < need) { ok = false; break; }
                }
                if (!ok) { b.gameObject.SetActive(false); continue; }
                b.position = p; b.transform.position = p; placed.Add(p);
            }
            for (int tries = 0; tries < 500; tries++)
            {
                var p = new Vector3((float)(rng.NextDouble() * 9.4 - 4.7), by, (float)(rng.NextDouble() * 14.0 - 7.0));
                bool ok = true; foreach (var q in placed) if ((q - p).magnitude < 0.6f) { ok = false; break; }
                if (!ok) continue;
                cueBall.velocity = Vector3.zero; cueBall.angularVelocity = Vector3.zero; cueBall.position = p; cueBall.transform.position = p; break;
            }
            Physics.SyncTransforms();
        };

        var writer = new StreamWriter(outFile, false);
        writer.WriteLine("# " + profile.name + " sightMarginBallRadii=" + margin + " seed=" + seed + " (each AI visit starts from a loosened partial pack + scattered reds)");
        writer.Flush();
        int visit = 1, visitsDone = 0;
        bool lastFoul = false;
        logCallback = (m, s, t) =>
        {
            try
            {
                if (!m.StartsWith("[AI:") || m.Contains("Ready as Player")) return;
                string line = m.Replace("[AI:" + profile.name + "] ", "");
                if (line.StartsWith("OUTCOME"))
                {
                    lastFoul = (bool)foulField.GetValue(gm);
                    line += " foul=" + lastFoul;
                }
                writer.WriteLine("V" + visit + " | " + line);
                writer.Flush();
                if (m.Contains("Visit over after")) { visit++; visitsDone++; }
                status = profile.name + " visits=" + visitsDone;
            }
            catch (System.Exception e) { status = "log error " + e.Message; }
        };
        Application.logMessageReceived += logCallback;

        int phase = 0, wait = 0;
        update = () =>
        {
            if (!Application.isPlaying) { Stop(); writer.Close(); return; }
            if (visitsDone >= maxVisits) { status = profile.name + " DONE visits=" + visitsDone; Stop(); writer.Close(); return; }
            if (gm.CurrentPlayerIndex == aiIdx || !gm.isNextPlay() || gm.IsStrikeRequested) { phase = 0; return; }
            if (gm.IsAwaitingPlacement) { gm.TryPlaceCueBall(gm.LastValidPlacement); gm.ConfirmPlacement(); return; }
            if (phase == 0)
            {
                layout();
                phase = 1; wait = 3; return;
            }
            if (phase == 1)
            {
                if (--wait > 0) return;
                // Only now is the human's stand-in shot aimed, from where the cue ball was put.
                Vector3 p = cueBall.position; float bestAz = 0; float bestFree = -1;
                for (int k = 0; k < 36; k++)
                {
                    float az = k * 10f; float r = az * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(-Mathf.Sin(r), 0, -Mathf.Cos(r));
                    float free = 0;
                    for (float d = 0.5f; d <= 4f; d += 0.5f) { if (cue.IsPathClear(p, p + dir * d, cueBall, null, true, 0.1f)) free = d; else break; }
                    if (free > bestFree) { bestFree = free; bestAz = az; }
                }
                cv.SetAzimuth(bestAz); phase = 2; wait = 5; return;
            }
            if (phase == 2)
            {
                if (--wait > 0) return;
                if (!gm.IsConfirmMode) gm.ConfirmButtonPressed();
                gm.SetStrikeForce(slider.MinPower);
                gm.RequestStrike(); phase = 3;
            }
        };
        EditorApplication.update += update;
        Time.maximumDeltaTime = 1f;
        Time.timeScale = 10f;
        return "running " + profile.name + " margin=" + margin + " -> " + outFile;
    }

    public static void Stop()
    {
        if (update != null) EditorApplication.update -= update;
        if (logCallback != null) Application.logMessageReceived -= logCallback;
        update = null; logCallback = null;
        Time.timeScale = 1f;
    }
}
