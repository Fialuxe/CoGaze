using UnityEngine;
using System.Collections;
#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR;
#endif

/// <summary>
/// Runs on the Local Worker (Quest 3/3S) only — attached inside LocalWorkerSetup's IsMine block.
///
/// Uses PassthroughCameraAccess (MRUK) to capture the physical front camera.
/// Frames are JPEG-encoded and sent to the Expert via IVideoTransport.
/// Editor falls back to a virtual capture camera for pipeline testing.
/// </summary>
public class WorkerVideoStream : MonoBehaviour
{
    [Header("Capture settings")]
    public Vector2Int requestedResolution = new Vector2Int(640, 480);
    [Range(1, 100)]
    public int jpegQuality = 40;
    [Tooltip("Seconds between frames sent. 0.1 = 10 fps.")]
    public float frameInterval = 0.1f;

    private ExperimentManager expManager;
    private Coroutine         streamCoroutine;
    private Texture2D         readbackTex;
    private IVideoTransport   transport;

    // ── PCA (Android / Quest 3+) ──────────────────────────────────────────
#if UNITY_ANDROID && !UNITY_EDITOR
    private PassthroughCameraAccess pca;
#endif

    // ── Virtual camera fallback (Editor) ──────────────────────────────────
#if UNITY_EDITOR
    private Camera        captureCamera;
    private RenderTexture captureRT;
#endif

    // ── Init ──────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager manager, IVideoTransport videoTransport)
    {
        expManager = manager;
        transport  = videoTransport;
        expManager.OnStateChanged += OnStateChanged;

#if UNITY_ANDROID && !UNITY_EDITOR
        SetupPCA();
#endif
    }

    private void OnDestroy()
    {
        if (readbackTex != null) Destroy(readbackTex);
#if UNITY_EDITOR
        if (captureRT != null) { captureRT.Release(); Destroy(captureRT); }
#endif
    }

    // ── PCA setup (Android / Quest 3+) ───────────────────────────────────

#if UNITY_ANDROID && !UNITY_EDITOR
    private void SetupPCA()
    {
        if (!PassthroughCameraAccess.IsSupported)
        {
            Debug.LogError("[WorkerVideoStream] PassthroughCameraAccess not supported on this device/OS version.");
            return;
        }
        pca = gameObject.AddComponent<PassthroughCameraAccess>();
        pca.CameraPosition      = PassthroughCameraAccess.CameraPositionType.Left;
        pca.RequestedResolution  = requestedResolution;
        pca.enabled = false; // enabled only while streaming
        Debug.Log("[WorkerVideoStream] PassthroughCameraAccess ready.");
    }
#endif

    // ── Experiment state ──────────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        bool shouldStream =
            (state == ExperimentState.TaskRunning   && expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && expManager.CurrentStepType == StepType.Alignment);
        if (shouldStream) StartStream(); else StopStream();
    }

    private void StartStream()
    {
        if (streamCoroutine != null) return;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (pca == null)
        {
            Debug.LogWarning("[WorkerVideoStream] PCA not available — stream not started.");
            return;
        }
        pca.enabled = true;
#else
        SetupVirtualCamera();
#endif

        streamCoroutine = StartCoroutine(StreamLoop());
        Debug.Log("[WorkerVideoStream] Streaming started.");
    }

    private void StopStream()
    {
        if (streamCoroutine == null) return;
        StopCoroutine(streamCoroutine);
        streamCoroutine = null;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (pca != null) pca.enabled = false;
#endif
        Debug.Log("[WorkerVideoStream] Streaming stopped.");
    }

    // ── Virtual camera setup (Editor only) ───────────────────────────────

#if UNITY_EDITOR
    private void SetupVirtualCamera()
    {
        if (captureCamera != null) return;

        int w = requestedResolution.x;
        int h = requestedResolution.y;
        captureRT   = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        readbackTex = new Texture2D(w, h, TextureFormat.RGB24, false);

        OVRCameraRig rig    = Object.FindAnyObjectByType<OVRCameraRig>();
        Transform    anchor = rig != null ? rig.centerEyeAnchor : transform;

        var camGo = new GameObject("VideoCaptureCam");
        camGo.transform.SetParent(anchor, false);
        camGo.transform.localPosition = Vector3.zero;
        camGo.transform.localRotation = Quaternion.identity;

        captureCamera = camGo.AddComponent<Camera>();
        captureCamera.fieldOfView     = 90f;
        captureCamera.nearClipPlane   = 0.05f;
        captureCamera.farClipPlane    = 100f;
        captureCamera.cullingMask     = ~0;
        captureCamera.clearFlags      = CameraClearFlags.SolidColor;
        captureCamera.backgroundColor = Color.black;
        captureCamera.targetTexture   = captureRT;
        captureCamera.enabled         = false;
    }
#endif

    // ── Stream loop ───────────────────────────────────────────────────────

    private IEnumerator StreamLoop()
    {
        var wait = new WaitForSeconds(frameInterval);
        while (true)
        {
            yield return wait;
            CaptureAndSend();
        }
    }

    private void CaptureAndSend()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SendPCAFrame();
#else
        SendVirtualFrame();
#endif
    }

    // ── PCA capture (Android / Quest 3+) ─────────────────────────────────

#if UNITY_ANDROID && !UNITY_EDITOR
    private void SendPCAFrame()
    {
        if (pca == null || !pca.IsPlaying) return;
        if (!pca.IsUpdatedThisFrame) return;

        var rt = pca.GetTexture() as RenderTexture;
        if (rt == null) return;

        int w = pca.CurrentResolution.x;
        int h = pca.CurrentResolution.y;

        if (readbackTex == null || readbackTex.width != w || readbackTex.height != h)
        {
            if (readbackTex != null) Destroy(readbackTex);
            readbackTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        }

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        readbackTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        readbackTex.Apply(false);
        RenderTexture.active = prev;

        byte[] jpeg = ImageConversion.EncodeToJPG(readbackTex, jpegQuality);
        transport?.Send(jpeg);
    }
#endif

    // ── Virtual camera capture (Editor only) ─────────────────────────────

#if UNITY_EDITOR
    private void SendVirtualFrame()
    {
        if (captureCamera == null || captureRT == null) return;

        captureCamera.Render();

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = captureRT;
        readbackTex.ReadPixels(new Rect(0, 0, requestedResolution.x, requestedResolution.y), 0, 0, false);
        readbackTex.Apply(false);
        RenderTexture.active = prev;

        transport?.Send(ImageConversion.EncodeToJPG(readbackTex, jpegQuality));
    }
#endif
}
