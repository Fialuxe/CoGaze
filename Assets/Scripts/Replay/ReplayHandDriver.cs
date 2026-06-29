using System;
using UnityEngine;

// Renders Worker hand skeletons from 24 OVR bone positions in ReplayFrameData; hidden for Assembly trials.
public class ReplayHandDriver : MonoBehaviour
{
    // OVR hand skeleton parent→child edge list (24-bone layout).
    // Index pairs reference positions in the 24-element bone array.
    private static readonly (int a, int b)[] k_boneEdges =
    {
        (0, 1),                               // Wrist → Forearm
        (0, 2), (2, 3), (3, 4), (4, 5), (5, 19),   // Thumb chain → Tip
        (0, 6), (6, 7), (7, 8), (8, 20),            // Index chain → Tip
        (0, 9), (9, 10), (10, 11), (11, 21),         // Middle chain → Tip
        (0, 12), (12, 13), (13, 14), (14, 22),       // Ring chain → Tip
        (0, 15), (15, 16), (16, 17), (17, 18), (18, 23) // Pinky chain → Tip
    };

    private ReplayManager   _mgr;
    private Transform[]     _leftBones;
    private Transform[]     _rightBones;
    private LineRenderer[]  _leftEdges;
    private LineRenderer[]  _rightEdges;

    public void Initialize(ReplayManager manager)
    {
        _mgr = manager;

        try
        {
            var leftColor  = new Color(0.3f, 0.8f, 1f, 0.9f);
            var rightColor = new Color(1f, 0.6f, 0.3f, 0.9f);

            _leftBones  = CreateBoneSpheres("LeftHand",  leftColor);
            _rightBones = CreateBoneSpheres("RightHand", rightColor);
            _leftEdges  = CreateEdgeLines("LeftEdges",  leftColor);
            _rightEdges = CreateEdgeLines("RightEdges", rightColor);

            SetHandVisible(_leftBones,  _leftEdges,  false);
            SetHandVisible(_rightBones, _rightEdges, false);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ReplayHandDriver] Setup failed: {ex.Message}");
            return;
        }

        _mgr.OnFrameChanged += OnFrameChanged;
    }

    // ── Scene construction ───────────────────────────────────────────────

    private Transform[] CreateBoneSpheres(string parentName, Color color)
    {
        var parent = new GameObject(parentName);
        var bones  = new Transform[24];
        var mat    = new Material(Shader.Find("Sprites/Default")) { color = color };

        for (int i = 0; i < 24; i++)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = $"Bone_{i}";
            sphere.transform.SetParent(parent.transform);
            sphere.transform.localScale = Vector3.one * 0.016f;
            var collider = sphere.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            sphere.GetComponent<MeshRenderer>().material = mat;
            bones[i] = sphere.transform;
        }

        return bones;
    }

    private LineRenderer[] CreateEdgeLines(string parentName, Color color)
    {
        var parent = new GameObject(parentName);
        var edges  = new LineRenderer[k_boneEdges.Length];
        var mat    = new Material(Shader.Find("Sprites/Default")) { color = color };

        for (int i = 0; i < k_boneEdges.Length; i++)
        {
            var go = new GameObject($"Edge_{i}");
            go.transform.SetParent(parent.transform);
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth = lr.endWidth = 0.005f;
            lr.useWorldSpace = true;
            lr.material      = mat;
            edges[i]         = lr;
        }

        return edges;
    }

    // ── Frame update ─────────────────────────────────────────────────────

    private void OnFrameChanged(ReplayFrameData frame, int _)
    {
        try
        {
            UpdateHand(frame.handL, _leftBones,  _leftEdges);
            UpdateHand(frame.handR, _rightBones, _rightEdges);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ReplayHandDriver] Frame update error: {ex.Message}");
            SetHandVisible(_leftBones,  _leftEdges,  false);
            SetHandVisible(_rightBones, _rightEdges, false);
        }
    }

    private void UpdateHand(float[][] handData, Transform[] bones, LineRenderer[] edges)
    {
        if (handData == null || handData.Length < 24)
        {
            SetHandVisible(bones, edges, false);
            return;
        }

        SetHandVisible(bones, edges, true);

        for (int i = 0; i < 24; i++)
        {
            if (handData[i] == null || handData[i].Length < 3) continue;
            bones[i].position = new Vector3(handData[i][0], handData[i][1], handData[i][2]);
        }

        for (int i = 0; i < k_boneEdges.Length && i < edges.Length; i++)
        {
            int a = k_boneEdges[i].a, b = k_boneEdges[i].b;
            if (a < bones.Length && b < bones.Length && bones[a] != null && bones[b] != null)
            {
                edges[i].SetPosition(0, bones[a].position);
                edges[i].SetPosition(1, bones[b].position);
            }
        }
    }

    private static void SetHandVisible(Transform[] bones, LineRenderer[] edges, bool visible)
    {
        if (bones != null)
            foreach (var b in bones)
                if (b != null) b.gameObject.SetActive(visible);

        if (edges != null)
            foreach (var e in edges)
                if (e != null) e.enabled = visible;
    }

    private void OnDestroy()
    {
        if (_mgr != null) _mgr.OnFrameChanged -= OnFrameChanged;
    }
}
