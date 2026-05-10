using System;
using UnityEngine;

/// <summary>
/// Drives the gaze visualization during replay. Creates a WorkerHead capsule,
/// a non-rendering camera for ray reconstruction, and the three visualizer components.
/// Replicates the same ray math as GazeVisualizer.Update().
/// Added by ReplayBootstrapper.
/// </summary>
public class ReplayGazeDriver : MonoBehaviour
{
    private const float EXPERT_FOV        = 60f;
    private const float STREAMING_FOV     = 90f;
    private const float STREAMING_ASPECT  = 4f / 3f;
    private const float DEFAULT_DISTANCE  = 3f;
    private const float MAX_RAY_DISTANCE  = 10f;

    private ReplayManager     mgr;
    private GameObject        workerHead;
    private GameObject        expertHead;
    private Camera            replayCamera;
    private RayVisualizer     rayViz;
    private CircleVisualizer  circleViz;
    private FrustumVisualizer frustumViz;
    private VisualizationMode activeMode;

    public void Initialize(ReplayManager manager)
    {
        mgr = manager;

        try
        {
            BuildScene();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ReplayGazeDriver] Scene build failed: {ex.Message}");
            return;
        }

        mgr.OnLoaded        += OnLoaded;
        mgr.OnFrameChanged  += OnFrameChanged;
    }

    private void BuildScene()
    {
        // Worker head capsule (blue-ish)
        workerHead = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        workerHead.name = "WorkerHead";
        workerHead.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
        var workerCol = workerHead.GetComponent<Collider>();
        if (workerCol != null) Destroy(workerCol);
        workerHead.GetComponent<MeshRenderer>().material.color = new Color(0.3f, 0.7f, 1f);
        workerHead.SetActive(false);

        // Expert head capsule (orange-ish)
        expertHead = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        expertHead.name = "ExpertHead";
        expertHead.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
        var expertCol = expertHead.GetComponent<Collider>();
        if (expertCol != null) Destroy(expertCol);
        expertHead.GetComponent<MeshRenderer>().material.color = new Color(1f, 0.55f, 0.2f);
        expertHead.SetActive(false);

        // Non-rendering camera for ray reconstruction — must follow Expert head,
        // because frame.gaze is Expert's normalized viewport coordinate.
        var camGo = new GameObject("ReplayCamera");
        camGo.transform.SetParent(expertHead.transform);
        camGo.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        replayCamera             = camGo.AddComponent<Camera>();
        replayCamera.enabled     = false;
        replayCamera.fieldOfView = EXPERT_FOV;

        // Visualizers at scene root — they use world-space coordinates, parenting doesn't matter
        var vizGo = new GameObject("GazeViz");
        rayViz    = vizGo.AddComponent<RayVisualizer>();
        circleViz = vizGo.AddComponent<CircleVisualizer>();
        frustumViz = vizGo.AddComponent<FrustumVisualizer>();

        HideAll();
        SetActiveVisualizer(VisualizationMode.Ray); // default; updated when file is loaded
    }

    // ── Callbacks ───────────────────────────────────────────────────────

    private void OnLoaded(ReplayData data)
    {
        workerHead.SetActive(true);
        expertHead.SetActive(true);

        // Parse gaze mode from meta
        if (!Enum.TryParse(data.meta?.gazeMode, out activeMode))
        {
            activeMode = VisualizationMode.Ray;
            Debug.LogWarning($"[ReplayGazeDriver] Unknown gazeMode '{data.meta?.gazeMode}', defaulting to Ray.");
        }

        SetActiveVisualizer(activeMode);

        // Set camera FOV to match the condition's task type
        bool isAssembly = string.Equals(data.meta?.stepType, StepType.Assembly.ToString(),
                                        StringComparison.OrdinalIgnoreCase);
        if (isAssembly)
        {
            replayCamera.fieldOfView = STREAMING_FOV;
            replayCamera.aspect      = STREAMING_ASPECT;
        }
        else
        {
            replayCamera.fieldOfView = EXPERT_FOV;
            replayCamera.ResetAspect();
        }

        if (activeMode == VisualizationMode.Frustum)
            frustumViz.SetCameraParams(replayCamera.fieldOfView, replayCamera.aspect);

        ApplyMeshTransform(data.meta);
    }

    private void ApplyMeshTransform(ReplayMeta meta)
    {
        if (meta?.meshPos == null || meta.meshPos.Length < 3) return;

        var meshObj = GameObject.Find("SharedMesh");
        if (meshObj == null)
        {
            Debug.LogWarning("[ReplayGazeDriver] 'SharedMesh' not found in replay scene — " +
                             "place the SharedMesh prefab in the scene for correct spatial context.");
            return;
        }

        meshObj.transform.SetPositionAndRotation(
            new Vector3(meta.meshPos[0], meta.meshPos[1], meta.meshPos[2]),
            meta.meshRot?.Length >= 4
                ? new Quaternion(meta.meshRot[0], meta.meshRot[1], meta.meshRot[2], meta.meshRot[3])
                : Quaternion.identity);

        if (meta.meshScale?.Length >= 3)
            meshObj.transform.localScale = new Vector3(meta.meshScale[0], meta.meshScale[1], meta.meshScale[2]);

        Debug.Log($"[ReplayGazeDriver] SharedMesh positioned at {meshObj.transform.position}");
    }

    private void OnFrameChanged(ReplayFrameData frame, int _)
    {
        try
        {
            ApplyFrame(frame);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ReplayGazeDriver] Frame apply error: {ex.Message}");
            HideAll();
        }
    }

    private void ApplyFrame(ReplayFrameData frame)
    {
        // Move Worker head
        if (frame.workerHead?.p?.Length >= 3 && frame.workerHead?.r?.Length >= 4)
        {
            workerHead.transform.SetPositionAndRotation(
                new Vector3(frame.workerHead.p[0], frame.workerHead.p[1], frame.workerHead.p[2]),
                new Quaternion(frame.workerHead.r[0], frame.workerHead.r[1], frame.workerHead.r[2], frame.workerHead.r[3]));
        }

        // Move Expert head
        if (frame.expertHead?.p?.Length >= 3 && frame.expertHead?.r?.Length >= 4)
        {
            expertHead.transform.SetPositionAndRotation(
                new Vector3(frame.expertHead.p[0], frame.expertHead.p[1], frame.expertHead.p[2]),
                new Quaternion(frame.expertHead.r[0], frame.expertHead.r[1], frame.expertHead.r[2], frame.expertHead.r[3]));
        }

        if (frame.gaze == null || frame.gaze.Length < 3) { HideAll(); return; }

        float x = frame.gaze[0], y = frame.gaze[1], blink = frame.gaze[2];
        if (blink > 0.5f) { HideAll(); return; }

        Ray ray = replayCamera.ViewportPointToRay(new Vector3(x, y, 0f));

        switch (activeMode)
        {
            case VisualizationMode.Ray:
            {
                Vector3 start = ray.origin + ray.direction * 0.5f;
                Vector3 end   = Physics.Raycast(ray, out RaycastHit hit, MAX_RAY_DISTANCE)
                    ? hit.point
                    : ray.GetPoint(DEFAULT_DISTANCE);
                rayViz.UpdateVisualization(start, end);
                break;
            }

            case VisualizationMode.Circle:
            {
                if (Physics.Raycast(ray, out RaycastHit hit, MAX_RAY_DISTANCE))
                {
                    circleViz.UpdateVisualization(hit.point, hit.normal);
                    circleViz.SetVisible(true);
                }
                else
                {
                    circleViz.SetVisible(false);
                }
                break;
            }

            case VisualizationMode.Frustum:
            {
                frustumViz.SetCameraParams(replayCamera.fieldOfView, replayCamera.aspect);
                frustumViz.UpdateVisualization(ray.origin, ray.direction, Vector3.zero, false);
                break;
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SetActiveVisualizer(VisualizationMode mode)
    {
        if (rayViz     != null) rayViz.enabled     = mode == VisualizationMode.Ray;
        if (circleViz  != null) circleViz.enabled  = mode == VisualizationMode.Circle;
        if (frustumViz != null) frustumViz.enabled = mode == VisualizationMode.Frustum;
    }

    private void HideAll()
    {
        rayViz?.SetVisible(false);
        circleViz?.SetVisible(false);
        frustumViz?.SetVisible(false);
    }

    private void OnDestroy()
    {
        if (mgr == null) return;
        mgr.OnLoaded       -= OnLoaded;
        mgr.OnFrameChanged -= OnFrameChanged;
    }
}
