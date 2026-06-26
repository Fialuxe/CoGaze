using UnityEngine;
using System.Collections;
using Unity.WebRTC;
#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR;
#endif

/// <summary>
/// Runs on the Local Worker (Quest 3) only.
///
/// Captures the passthrough camera via PassthroughCameraAccess and blits each
/// frame into a stable RenderTexture.  WebRtcVideoSession reads that texture and
/// encodes it with the Snapdragon hardware H.264 encoder — no CPU JPEG, no ReadPixels.
///
/// Call TriggerOffer() once the Expert is confirmed in the Photon room so that
/// WebRtcVideoSession initiates the SDP offer.
/// </summary>
public class WorkerVideoStream : MonoBehaviour
{
    [Header("Capture")]
    public Vector2Int requestedResolution = new Vector2Int(640, 480);
    [Tooltip("Seconds between blit ticks. 0.033 ≈ 30 fps.")]
    public float frameInterval = 0.033f;

    private ExperimentManager2 expManager;
    private WebRtcVideoSession session;
    private RenderTexture      captureRT;
    private Coroutine          streamCoroutine;

#if UNITY_ANDROID && !UNITY_EDITOR
    private PassthroughCameraAccess pca;
#endif

#if UNITY_EDITOR
    private Camera        editorCam;
    private RenderTexture editorRT;
#endif

    // ── Init ────────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager2 manager)
    {
        expManager = manager;
        expManager.OnStateChanged += OnStateChanged;

        // Format must match what Unity WebRTC expects for the current graphics API.
        // OpenGLES3 (Quest default) needs R8G8B8A8; Vulkan/D3D needs B8G8R8A8.
        // Using the wrong format throws ArgumentException inside VideoStreamTrack constructor.
        var rtFormat = WebRTC.GetSupportedRenderTextureFormat(SystemInfo.graphicsDeviceType);
        captureRT = new RenderTexture(requestedResolution.x, requestedResolution.y, 0, rtFormat);
        captureRT.Create();

        session = gameObject.AddComponent<WebRtcVideoSession>();

#if UNITY_ANDROID && !UNITY_EDITOR
        SetupPCA();
#endif
    }

    /// <summary>
    /// Called by SceneBootstrapper2 once the Expert is in the room.
    /// Starts the WebRTC handshake. Signaling callbacks must already be wired
    /// before this is called (done in SceneBootstrapper2).
    /// </summary>
    public void TriggerOffer()
    {
        Debug.Log($"[WorkerVideoStream] TriggerOffer called. RT={captureRT?.width}x{captureRT?.height} fmt={captureRT?.graphicsFormat} created={captureRT?.IsCreated()} gfx={SystemInfo.graphicsDeviceType}");
        session.StartAsOfferer(captureRT);
    }

    public WebRtcVideoSession Session => session;

    // ── PCA setup ───────────────────────────────────────────────────────────

#if UNITY_ANDROID && !UNITY_EDITOR
    private void SetupPCA()
    {
        if (!PassthroughCameraAccess.IsSupported)
        {
            Debug.LogError("[WorkerVideoStream] PassthroughCameraAccess not supported.");
            return;
        }
        pca = gameObject.AddComponent<PassthroughCameraAccess>();
        pca.CameraPosition      = PassthroughCameraAccess.CameraPositionType.Left;
        pca.RequestedResolution = requestedResolution;
        pca.enabled = false;
        FileLogger.Log("Transport", "[WorkerVideoStream] PCA ready.");
    }
#endif

    // ── Experiment state ─────────────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        bool active =
            (state == ExperimentState.TaskRunning   && expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && expManager.CurrentStepType == StepType.Alignment);
        Debug.Log($"[WorkerVideoStream] OnStateChanged state={state} stepType={expManager.CurrentStepType} active={active}");
        if (active) StartCapture(); else StopCapture();
    }

    private void StartCapture()
    {
        if (streamCoroutine != null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (pca != null) pca.enabled = true;
#else
        SetupEditorCamera();
#endif
        streamCoroutine = StartCoroutine(CaptureLoop());
        FileLogger.Log("Transport", "[WorkerVideoStream] Capture started.");
    }

    private void StopCapture()
    {
        if (streamCoroutine == null) return;
        StopCoroutine(streamCoroutine);
        streamCoroutine = null;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (pca != null) pca.enabled = false;
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
            if (pca == null || !pca.IsPlaying || !pca.IsUpdatedThisFrame) continue;
            var src = pca.GetTexture();
            if (src != null) Graphics.Blit(src, captureRT);
#elif UNITY_EDITOR
            if (editorCam == null || editorRT == null) continue;
            editorCam.Render();
            Graphics.Blit(editorRT, captureRT);
#endif
        }
    }

    // ── Editor fallback camera ────────────────────────────────────────────────

#if UNITY_EDITOR
    private void SetupEditorCamera()
    {
        if (editorCam != null) return;
        int w = requestedResolution.x, h = requestedResolution.y;
        editorRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);

        OVRCameraRig rig    = Object.FindAnyObjectByType<OVRCameraRig>();
        Transform    anchor = rig != null ? rig.centerEyeAnchor : transform;

        var go = new GameObject("EditorCaptureCam");
        go.transform.SetParent(anchor, false);
        editorCam = go.AddComponent<Camera>();
        editorCam.fieldOfView   = 90f;
        editorCam.nearClipPlane = 0.05f;
        editorCam.farClipPlane  = 100f;
        editorCam.targetTexture = editorRT;
        editorCam.enabled       = false;
    }
#endif

    private void OnDestroy()
    {
        if (expManager != null) expManager.OnStateChanged -= OnStateChanged;
        session?.Stop();
        captureRT?.Release();
        if (captureRT != null) Destroy(captureRT);
#if UNITY_EDITOR
        if (editorRT != null) { editorRT.Release(); Destroy(editorRT); }
#endif
    }
}
