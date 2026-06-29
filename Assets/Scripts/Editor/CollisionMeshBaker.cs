using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityMeshSimplifier;

// Editor tool (CoGaze menu): bakes simplified collision meshes for SharedMesh; saves to Assets/GeneratedMeshes.
public static class CollisionMeshBaker
{
    private const string k_outputFolder  = "Assets/GeneratedMeshes";
    private const string k_previewTag    = "[CollisionPreview]";

    // ── Bake ──────────────────────────────────────────────────────────────────

    [MenuItem("CoGaze/Bake Collision Mesh")]
    private static void Bake()
    {
        var (handler, meshGo) = FindTargets();
        if (handler == null || meshGo == null) return;

        var so      = new SerializedObject(handler);
        float quality = so.FindProperty("collisionMeshQuality").floatValue;

        // MeshColliders are added at runtime in Start() — use MeshFilter which exists in Editor.
        var filters = meshGo.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0)
        {
            EditorUtility.DisplayDialog("Bake Collision Mesh",
                $"'{meshGo.name}' has no MeshFilters.\n" +
                "Make sure the SharedMesh object is placed in the current scene.", "OK");
            return;
        }

        Ensurek_outputFolder();

        var baked = new List<Mesh>();
        var stats = new StringBuilder();
        stats.AppendLine($"Quality: {quality:F3}  (0=最小, 1=原型維持)");
        stats.AppendLine();

        foreach (var mf in filters)
        {
            if (mf.sharedMesh == null) continue;
            int srcTris = mf.sharedMesh.triangles.Length / 3;
            Mesh simplified = Simplify(mf.sharedMesh, quality);
            int dstTris = simplified.triangles.Length / 3;

            string path = $"{k_outputFolder}/{mf.name}_collision.mesh";
            AssetDatabase.CreateAsset(simplified, path);
            baked.Add(AssetDatabase.LoadAssetAtPath<Mesh>(path));

            float ratio = srcTris > 0 ? (float)dstTris / srcTris * 100f : 0f;
            stats.AppendLine($"{mf.name}");
            stats.AppendLine($"  {srcTris,10:N0} tris  →  {dstTris,7:N0} tris  ({ratio:F1}%)");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Auto-assign to MeshHandler.bakedCollisionMeshes
        var prop = so.FindProperty("bakedCollisionMeshes");
        prop.arraySize = baked.Count;
        for (int i = 0; i < baked.Count; i++)
            prop.GetArrayElementAtIndex(i).objectReferenceValue = baked[i];
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(handler);

        EditorUtility.DisplayDialog("Bake Collision Mesh — Done", stats.ToString(), "OK");

        if (baked.Count > 0)
            EditorGUIUtility.PingObject(baked[0]);
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    [MenuItem("CoGaze/Preview Collision Mesh (Scene)")]
    private static void Preview()
    {
        // Remove any existing preview first
        var existing = GameObject.Find(k_previewTag);
        if (existing != null)
        {
            Object.DestroyImmediate(existing);
            Debug.Log("[CollisionMeshBaker] Preview removed.");
        }

        var (handler, meshGo) = FindTargets();
        if (handler == null || meshGo == null) return;

        var so       = new SerializedObject(handler);
        var bakeProp = so.FindProperty("bakedCollisionMeshes");
        float quality = so.FindProperty("collisionMeshQuality").floatValue;

        // MeshColliders are added at runtime — use MeshFilter which exists in Editor.
        var filters = meshGo.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0)
        {
            EditorUtility.DisplayDialog("Preview Collision Mesh", "No MeshFilters found on SharedMesh.", "OK");
            return;
        }

        var previewRoot = new GameObject(k_previewTag);

        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i].sharedMesh == null) continue;

            // Use already-baked mesh if available, otherwise compute now for preview
            Mesh mesh = null;
            if (bakeProp.arraySize > i)
                mesh = bakeProp.GetArrayElementAtIndex(i).objectReferenceValue as Mesh;
            if (mesh == null)
                mesh = Simplify(filters[i].sharedMesh, quality);

            var child = new GameObject($"Preview_{filters[i].name}");
            child.transform.SetParent(previewRoot.transform, false);
            child.transform.SetPositionAndRotation(
                filters[i].transform.position,
                filters[i].transform.rotation);
            child.transform.localScale = filters[i].transform.lossyScale;

            var mf = child.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = child.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Unlit/Color"));
            mat.color = new Color(0f, 1f, 0.5f, 1f);
            mr.sharedMaterial = mat;
        }

        Selection.activeGameObject = previewRoot;
        SceneView.lastActiveSceneView?.FrameSelected();
        Debug.Log($"[CollisionMeshBaker] Preview created: {previewRoot.name} — delete it when done.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (MeshHandler handler, GameObject meshGo) FindTargets()
    {
        var handler = Object.FindAnyObjectByType<MeshHandler>();
        if (handler == null)
        {
            EditorUtility.DisplayDialog("Bake Collision Mesh",
                "No MeshHandler found in the open scene.", "OK");
            return (null, null);
        }

        var so = new SerializedObject(handler);
        string name = so.FindProperty("meshObjectName").stringValue;
        var meshGo = GameObject.Find(name);
        if (meshGo == null)
        {
            EditorUtility.DisplayDialog("Bake Collision Mesh",
                $"GameObject '{name}' not found in scene.\n" +
                "Make sure the SharedMesh object is placed in the current scene.", "OK");
            return (null, null);
        }

        return (handler, meshGo);
    }

    private static void Ensurek_outputFolder()
    {
        if (!AssetDatabase.IsValidFolder(k_outputFolder))
            AssetDatabase.CreateFolder("Assets", "GeneratedMeshes");
    }

    // ── QEM simplification via UnityMeshSimplifier (Blender Decimate相当) ────

    internal static Mesh Simplify(Mesh source, float quality)
    {
        var simplifier = new MeshSimplifier();
        simplifier.Initialize(source);
        simplifier.SimplifyMesh(quality);
        Mesh result = simplifier.ToMesh();
        result.name = source.name + "_collision";
        return result;
    }
}
