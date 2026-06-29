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
#else
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

    // ── Capture loop ─────────────────────────────────────────────────────────

    private IEnumerator CaptureLoop()
    {
        var wait = new WaitForSeconds(frameInterval);
        while (true)
        {
            yield return wait;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_pca == null || !_pca.IsPlaying || !_pca.IsUpdatedThisFrame) continue;
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
        if (_expManager != null) _expManager.OnStateChanged -= OnStateChanged;
        _session?.Stop();
        _captureRT?.Release();
        if (_captureRT != null) Destroy(_captureRT);
#if UNITY_EDITOR
        if (_editorRT != null) { _editorRT.Release(); Destroy(_editorRT); }
#endif
    }
}
