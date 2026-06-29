using System.Collections.Generic;
using UnityEngine;

// ── Shared experiment types ───────────────────────────────────────────────────
// These are top-level so that ExperimentManager2, ExpertUI2, WorkerHUD2 etc.
// can all use them without any namespace prefix.

public enum ConditionType { IR, Webcam, WebcamFiltered, NoGaze }
public enum GazeMode      { Ray, Circle, Frustum, None }

[System.Serializable]
public struct ConditionDef
{
    public string        name;
    public ConditionType noise;  // which noise / tracking method
    public GazeMode      gaze;
}

// 10 conditions (3 gaze modes × 3 noise + NoGaze); Williams-balanced counterbalancing over 24 participant order indices.
public static class ExperimentDesign
{
    // ── Condition table ───────────────────────────────────────────────────────
    // Index: 0-2 = IR, 3-5 = Webcam, 6-8 = WebcamFiltered, 9 = NoGaze

    public static readonly ConditionDef[] Conditions = new ConditionDef[10]
    {
        new() { name = "IR_Ray",                 noise = ConditionType.IR,             gaze = GazeMode.Ray     },
        new() { name = "IR_Circle",              noise = ConditionType.IR,             gaze = GazeMode.Circle  },
        new() { name = "IR_Frustum",             noise = ConditionType.IR,             gaze = GazeMode.Frustum },
        new() { name = "Webcam_Ray",             noise = ConditionType.Webcam,         gaze = GazeMode.Ray     },
        new() { name = "Webcam_Circle",          noise = ConditionType.Webcam,         gaze = GazeMode.Circle  },
        new() { name = "Webcam_Frustum",         noise = ConditionType.Webcam,         gaze = GazeMode.Frustum },
        new() { name = "WebcamFiltered_Ray",     noise = ConditionType.WebcamFiltered, gaze = GazeMode.Ray     },
        new() { name = "WebcamFiltered_Circle",  noise = ConditionType.WebcamFiltered, gaze = GazeMode.Circle  },
        new() { name = "WebcamFiltered_Frustum", noise = ConditionType.WebcamFiltered, gaze = GazeMode.Frustum },
        new() { name = "NoGaze",                 noise = ConditionType.NoGaze,         gaze = GazeMode.None    },
    };

    // ── Two-level counterbalancing ─────────────────────────────────────────────
    // Group 0 = IR (0-2), Group 1 = Webcam (3-5), Group 2 = WebcamFiltered (6-8), Group 3 = NoGaze (9)

    private static readonly int[][] s_conditionGroups =
    {
        new[] { 0, 1, 2 },  // IR
        new[] { 3, 4, 5 },  // Webcam
        new[] { 6, 7, 8 },  // WebcamFiltered
        new[] { 9 },        // NoGaze
    };

    // Williams balanced Latin square for 4 groups.
    // Each group appears in each position exactly once;
    // each ordered pair (i, j) appears exactly once as adjacent groups.
    private static readonly int[][] s_groupOrderTable =
    {
        new[] { 0, 1, 3, 2 },
        new[] { 1, 2, 0, 3 },
        new[] { 2, 3, 1, 0 },
        new[] { 3, 0, 2, 1 },
    };

    // All 6 permutations of gaze modes {Ray=0, Circle=1, Frustum=2}.
    private static readonly int[][] s_gazeModeOrderTable =
    {
        new[] { 0, 1, 2 },
        new[] { 0, 2, 1 },
        new[] { 1, 0, 2 },
        new[] { 1, 2, 0 },
        new[] { 2, 0, 1 },
        new[] { 2, 1, 0 },
    };

    // ── Public API ─────────────────────────────────────────────────────────────

    public static int[] ComputeOrder(int participantOrderIndex)
    {
        int idx      = Mathf.Clamp(participantOrderIndex, 0, 23);
        int[] groups = s_groupOrderTable[idx / 6];
        int[] gazes  = s_gazeModeOrderTable[idx % 6];

        var order = new List<int>(10);
        foreach (int g in groups)
        {
            int[] group = s_conditionGroups[g];
            if (group.Length == 1)
                order.Add(group[0]);
            else
                foreach (int gi in gazes)
                    order.Add(group[gi]);
        }
        return order.ToArray();
    }

    public static VisualizationMode ToVisualizationMode(GazeMode mode) => mode switch
    {
        GazeMode.Circle  => VisualizationMode.Circle,
        GazeMode.Frustum => VisualizationMode.Frustum,
        GazeMode.None    => VisualizationMode.None,
        _                => VisualizationMode.Ray,
    };

    public static (string gaze, string noise) GetConditionInfo(int idx)
    {
        if (idx < 0 || idx >= Conditions.Length) return ("unknown", "unknown");
        var c = Conditions[idx];
        if (c.gaze == GazeMode.None) return ("none", "none");
        return (ToVisualizationMode(c.gaze).ToString().ToLowerInvariant(),
                c.noise.ToString().ToLowerInvariant());
    }
}
