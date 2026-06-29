using System;
using UnityEngine;

// Drives gaze visualization during replay; replicates GazeVisualizer.Update() ray math with non-rendering camera.
public class ReplayGazeDriver : MonoBehaviour
{
    private const float k_expertFov        = 60f;
    private const float k_streamingFov     = 90f;
    private const float k_streamingAspect  = 4f / 3f;
    private const float k_defaultDistance  = 3f;
    private const float k_maxRayDistance  = 10f;

    private ReplayManager     _mgr;
    private GameObject        _workerHead;
    private GameObject        _expertHead;
    private Camera            _replayCamera;
    private RayVisualizer     _rayViz;
    private CircleVisualizer  _circleViz;
    private FrustumVisualizer _frustumViz;
    private VisualizationMode _activeMode;

    public void Initialize(ReplayManager manager)
    {
        _mgr = manager;

        try
        {
            BuildScene();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ReplayGazeDriver] Scene build failed: {ex.Message}");
            return;
        }

        _mgr.OnLoaded        += OnLoaded;
        _mgr.OnFrameChanged  += OnFrameChanged;
    }

    private void BuildScene()
    {
        // Worker head capsule (blue-ish)
        _workerHead = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _workerHead.name = "WorkerHead";
        _workerHead.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
        var workerCol = _workerHead.GetComponent<Collider>();
        if (workerCol != null) Destroy(workerCol);
        _workerHead.GetComponent<MeshRenderer>().material.color = new Color(0.3f, 0.7f, 1f);
        _workerHead.SetActive(false);

        // Expert head capsule (orange-ish)
        _expertHead = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _expertHead.name = "ExpertHead";
        _expertHead.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
        var expertCol = _expertHead.GetComponent<Collider>();
        if (expertCol != null) Destroy(expertCol);
        _expertHead.GetComponent<MeshRenderer>().material.color = new Color(1f, 0.55f, 0.2f);
        _expertHead.SetActive(false);

        // Non-rendering camera for ray reconstruction — must follow Expert head,
        // because frame.gaze is Expert's normalized viewport coordinate.
        var camGo = new GameObject("ReplayCamera");
        camGo.transform.SetParent(_expertHead.transform);
        camGo.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        _replayCamera             = camGo.AddComponent<Camera>();
        _replayCamera.enabled     = false;
        _replayCamera.fieldOfView = k_expertFov;

        // Visualizers at scene root — they use world-space coordinates, parenting doesn't matter
        var vizGo = new GameObject("GazeViz");
        _rayViz    = vizGo.AddComponent<RayVisualizer>();
        _circleViz = vizGo.AddComponent<CircleVisualizer>();
        _frustumViz = vizGo.AddComponent<FrustumVisualizer>();

        HideAll();
        SetActiveVisualizer(VisualizationMode.Ray); // default; updated when file is loaded
    }

    // ── Callbacks ───────────────────────────────────────────────────────

    private void OnLoaded(ReplayData data)
    {
        _workerHead.SetActive(true);
        _expertHead.SetActive(true);

        // Parse gaze mode from meta
        if (!Enum.TryParse(data.meta?.gazeMode, out _activeMode))
        {
            _activeMode = VisualizationMode.Ray;
            Debug.LogWarning($"[ReplayGazeDriver] Unknown gazeMode '{data.meta?.gazeMode}', defaulting to Ray.");
        }

        SetActiveVisualizer(_activeMode);

        // Set camera FOV to match the condition's task type
        bool isAssembly = string.Equals(data.meta?.stepType, StepType.Assembly.ToString(),
                                        StringComparison.OrdinalIgnoreCase);
        if (isAssembly)
        {
            _replayCamera.fieldOfView = k_streamingFov;
            _replayCamera.aspect      = k_streamingAspect;
        }
        else
        {
            _replayCamera.fieldOfView = k_expertFov;
            _replayCamera.ResetAspect();
        }

        if (_activeMode == VisualizationMode.Frustum)
            _frustumViz.SetCameraParams(_replayCamera.fieldOfView, _replayCamera.aspect);

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
            _workerHead.transform.SetPositionAndRotation(
                new Vector3(frame.workerHead.p[0], frame.workerHead.p[1], frame.workerHead.p[2]),
                new Quaternion(frame.workerHead.r[0], frame.workerHead.r[1], frame.workerHead.r[2], frame.workerHead.r[3]));
        }

        // Move Expert head
        if (frame.expertHead?.p?.Length >= 3 && frame.expertHead?.r?.Length >= 4)
        {
            _expertHead.transform.SetPositionAndRotation(
                new Vector3(frame.expertHead.p[0], frame.expertHead.p[1], frame.expertHead.p[2]),
                new Quaternion(frame.expertHead.r[0], frame.expertHead.r[1], frame.expertHead.r[2], frame.expertHead.r[3]));
        }

        if (frame.gaze == null || frame.gaze.Length < 3) { HideAll(); return; }

        float x = frame.gaze[0], y = frame.gaze[1], blink = frame.gaze[2];
        if (blink > 0.5f) { HideAll(); return; }

        Ray ray = _replayCamera.ViewportPointToRay(new Vector3(x, y, 0f));

        switch (_activeMode)
        {
            case VisualizationMode.Ray:
            {
                Vector3 start = ray.origin + ray.direction * 0.5f;
                Vector3 end   = Physics.Raycast(ray, out RaycastHit hit, k_maxRayDistance)
                    ? hit.point
                    : ray.GetPoint(k_defaultDistance);
                _rayViz.UpdateVisualization(start, end);
                break;
            }

            case VisualizationMode.Circle:
            {
                if (Physics.Raycast(ray, out RaycastHit hit, k_maxRayDistance))
                {
                    _circleViz.UpdateVisualization(hit.point, hit.normal);
                    _circleViz.SetVisible(true);
                }
                else
                {
                    _circleViz.SetVisible(false);
                }
                break;
            }

            case VisualizationMode.Frustum:
            {
                _frustumViz.SetCameraParams(_replayCamera.fieldOfView, _replayCamera.aspect);
                _frustumViz.UpdateVisualization(ray.origin, ray.direction, Vector3.zero, false);
                break;
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SetActiveVisualizer(VisualizationMode mode)
    {
        if (_rayViz     != null) _rayViz.enabled     = mode == VisualizationMode.Ray;
        if (_circleViz  != null) _circleViz.enabled  = mode == VisualizationMode.Circle;
        if (_frustumViz != null) _frustumViz.enabled = mode == VisualizationMode.Frustum;
    }

    private void HideAll()
    {
        _rayViz?.SetVisible(false);
        _circleViz?.SetVisible(false);
        _frustumViz?.SetVisible(false);
    }

    private void OnDestroy()
    {
        if (_mgr == null) return;
        _mgr.OnLoaded       -= OnLoaded;
        _mgr.OnFrameChanged -= OnFrameChanged;
    }
}
