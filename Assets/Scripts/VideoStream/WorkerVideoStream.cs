using UnityEngine;
using System.Collections;
using Unity.WebRTC;
#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR;
#endif

// Worker (Quest 3): blits passthrough camera into RenderTexture; WebRtcVideoSession encodes with Snapdragon H.264.
public class WorkerVideoStream : MonoBehaviour
{
    [Header("Capture")]
    public Vector2Int requestedResolution = new Vector2Int(640, 480);
    [Tooltip("Seconds between blit ticks. 0.033 ≈ 30 fps.")]
    public float frameInterval = 0.033f;

    private ExperimentManager2 _expManager;
    private WebRtcVideoSession _session;
    private RenderTexture      _captureRT;
    private Coroutine          _streamCoroutine;

    // GazeFix scene only: lets GazeVisualizer reconstruct gaze rays from the Worker-local
    // PCA camera pose (Fix 3) and receive the real PCA intrinsics (Fix 2).
    private static WorkerVideoStream s_instance;
    private bool _intrinsicsPushed;

    // GazeFix HUD diagnostics (DebugHUD): whether REAL intrinsics were pushed and what values.
    // Statics live outside platform ifdefs so DebugHUD compiles on every platform; they are
    // only ever assigned on the Quest (inside PushRealIntrinsics).
    public static bool  GazeFixIntrinsicsPushed { get; private set; }
    public static float GazeFixVFov   { get; private set; }
    public static float GazeFixAspect { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
    private PassthroughCameraAccess _pca;
#endif

#if UNITY_EDITOR
    private Camera        _editorCam;
    private RenderTexture _editorRT;
#endif

    // ── Init ────────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager2 manager)
    {
        s_instance  = this;
        GazeFixIntrinsicsPushed = false;   // stale static from a previous run would mislead the HUD
        _expManager = manager;
        _expManager.OnStateChanged += OnStateChanged;

        // Format must match what Unity WebRTC expects for the current graphics API.
        // OpenGLES3 (Quest default) needs R8G8B8A8; Vulkan/D3D needs B8G8R8A8.
        // Using the wrong format throws ArgumentException inside VideoStreamTrack constructor.
        var rtFormat = WebRTC.GetSupportedRenderTextureFormat(SystemInfo.graphicsDeviceType);
        _captureRT = new RenderTexture(requestedResolution.x, requestedResolution.y, 0, rtFormat);
        _captureRT.Create();

        _session = gameObject.AddComponent<WebRtcVideoSession>();

#if UNITY_ANDROID && !UNITY_EDITOR
        SetupPCA();
#endif
    }

    public void TriggerOffer()
    {
        Debug.Log($"[WorkerVideoStream] TriggerOffer called. RT={_captureRT?.width}x{_captureRT?.height} fmt={_captureRT?.graphicsFormat} created={_captureRT?.IsCreated()} gfx={SystemInfo.graphicsDeviceType}");
        StartCapture();   // begin feeding frames so the Expert sees video during Setup
        _session.StartAsOfferer(_captureRT);
    }

    public WebRtcVideoSession Session => _session;

    // ── PCA setup ───────────────────────────────────────────────────────────

#if UNITY_ANDROID && !UNITY_EDITOR
    private void SetupPCA()
    {
        if (!PassthroughCameraAccess.IsSupported)
        {
            Debug.LogError("[WorkerVideoStream] PassthroughCameraAccess not supported.");
            return;
        }
        _pca = gameObject.AddComponent<PassthroughCameraAccess>();
        _pca.CameraPosition      = PassthroughCameraAccess.CameraPositionType.Left;
        _pca.RequestedResolution = requestedResolution;
        _pca.enabled = false;
        FileLogger.Log("Transport", "[WorkerVideoStream] PCA ready.");
    }
#endif

    // ── Experiment state ─────────────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        bool active =
            (state == ExperimentState.TaskRunning   && _expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && _expManager.CurrentStepType == StepType.Alignment);
        Debug.Log($"[WorkerVideoStream] OnStateChanged state={state} stepType={_expManager.CurrentStepType} active={active}");
        if (active) StartCapture(); else StopCapture();
    }

    private void StartCapture()
    {
        if (_streamCoroutine != null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_pca != null) _pca.enabled = true;
#elif UNITY_EDITOR
        SetupEditorCamera();
#endif
        _streamCoroutine = StartCoroutine(CaptureLoop());
        FileLogger.Log("Transport", "[WorkerVideoStream] Capture started.");
    }

    private void StopCapture()
    {
        if (_streamCoroutine == null) return;
        StopCoroutine(_streamCoroutine);
        _streamCoroutine = null;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_pca != null) _pca.enabled = false;
#endif
        FileLogger.Log("Transport", "[WorkerVideoStream] Capture stopped.");
    }

    // ── GazeFix scene: PCA pose / intrinsics for gaze ray reconstruction ─────

    // Fix 3: world-space pose of the left passthrough camera at the latest frame's timestamp.
    // Returns false when unavailable (Expert PC build, Editor, PCA not yet playing) so the
    // caller falls back to the legacy Photon-synced transform.
    public static bool TryGetPcaCameraPose(out Pose pose)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (s_instance != null && s_instance._pca != null && s_instance._pca.IsPlaying)
        {
            pose = s_instance._pca.GetCameraPose();
            return pose.rotation.x != 0f || pose.rotation.y != 0f
                || pose.rotation.z != 0f || pose.rotation.w != 0f; // default(Pose) = query failed
        }
#elif UNITY_EDITOR
        if (s_instance != null && s_instance._editorCam != null)
        {
            var t = s_instance._editorCam.transform;
            pose = new Pose(t.position, t.rotation);
            return true;
        }
#endif
        pose = default;
        return false;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // Fix 2: convert the PCA's sensor-space intrinsics into the vertical FOV / aspect /
    // principal-point viewport offset of the actually-streamed (centre-cropped, scaled) image,
    // and hand them to every GazeVisualizer. Replaces the guessed FOV=90°, 4:3.
    private void PushRealIntrinsics()
    {
        var cfg = GazeProjectionFixConfig.Instance;
        if (cfg == null || !cfg.useRealIntrinsics) { _intrinsicsPushed = true; return; }

        var intr = _pca.Intrinsics;
        Vector2 sensor  = intr.SensorResolution;
        Vector2 current = _pca.CurrentResolution;
        if (intr.FocalLength.x <= 0f || intr.FocalLength.y <= 0f ||
            sensor.x <= 0f || sensor.y <= 0f || current.x <= 0f || current.y <= 0f)
            return; // not ready yet — retried from CaptureLoop

        // The PCA centre-crops the sensor to the requested aspect before scaling
        // (same maths as its internal CalcSensorCropRegion).
        Vector2 scale = new Vector2(current.x / sensor.x, current.y / sensor.y);
        scale /= Mathf.Max(scale.x, scale.y);
        Vector2 cropSize = Vector2.Scale(sensor, scale);
        Vector2 cropMin  = (sensor - cropSize) * 0.5f;

        float vFov   = 2f * Mathf.Atan(cropSize.y / (2f * intr.FocalLength.y)) * Mathf.Rad2Deg;
        float aspect = (cropSize.x * intr.FocalLength.y) / (cropSize.y * intr.FocalLength.x);

        // Principal-point deviation from the crop centre, as a viewport-space offset.
        // Viewport y is bottom-up while image y is top-down, hence the flip.
        float cxNorm = (intr.PrincipalPoint.x - cropMin.x) / cropSize.x;
        float cyNorm = 1f - (intr.PrincipalPoint.y - cropMin.y) / cropSize.y;
        var ppOffset = new Vector2(0.5f - cxNorm, 0.5f - cyNorm);

        foreach (var viz in FindObjectsByType<GazeVisualizer>(FindObjectsSortMode.None))
        {
            viz.SetStreamingCameraParams(vFov, aspect);
            viz.SetStreamingPrincipalPointOffset(ppOffset);
        }
        _intrinsicsPushed = true;
        GazeFixIntrinsicsPushed = true;   // HUD diagnostics
        GazeFixVFov   = vFov;
        GazeFixAspect = aspect;
        FileLogger.Log("Transport",
            $"[WorkerVideoStream] GazeFix intrinsics: sensor={sensor} current={current} " +
            $"f={intr.FocalLength} pp={intr.PrincipalPoint} → vFOV={vFov:F2}° aspect={aspect:F4} ppOffset={ppOffset}");
    }
#endif

    // ── Capture loop ─────────────────────────────────────────────────────────

    private IEnumerator CaptureLoop()
    {
        var wait = new WaitForSeconds(frameInterval);
        while (true)
        {
            yield return wait;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_pca == null || !_pca.IsPlaying || !_pca.IsUpdatedThisFrame) continue;
            if (!_intrinsicsPushed) PushRealIntrinsics();
            var src = _pca.GetTexture();
            if (src != null) Graphics.Blit(src, _captureRT);
#elif UNITY_EDITOR
            if (_editorCam == null || _editorRT == null) continue;
            _editorCam.Render();
            Graphics.Blit(_editorRT, _captureRT);
#endif
        }
    }

    // ── Editor fallback camera ────────────────────────────────────────────────

#if UNITY_EDITOR
    private void SetupEditorCamera()
    {
        if (_editorCam != null) return;
        int w = requestedResolution.x, h = requestedResolution.y;
        _editorRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);

        OVRCameraRig rig    = Object.FindAnyObjectByType<OVRCameraRig>();
        Transform    anchor = rig != null ? rig.centerEyeAnchor : transform;

        var go = new GameObject("EditorCaptureCam");
        go.transform.SetParent(anchor, false);
        _editorCam = go.AddComponent<Camera>();
        _editorCam.fieldOfView   = 90f;
        _editorCam.nearClipPlane = 0.05f;
        _editorCam.farClipPlane  = 100f;
        _editorCam.targetTexture = _editorRT;
        _editorCam.enabled       = false;
    }
#endif

    private void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
        if (_expManager != null) _expManager.OnStateChanged -= OnStateChanged;
        _session?.Stop();
        _captureRT?.Release();
        if (_captureRT != null) Destroy(_captureRT);
#if UNITY_EDITOR
        if (_editorRT != null) { _editorRT.Release(); Destroy(_editorRT); }
#endif
    }
}
