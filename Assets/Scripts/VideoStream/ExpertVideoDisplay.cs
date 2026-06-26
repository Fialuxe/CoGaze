using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Runs on the Remote Expert (PC) only.
///
/// Creates a fullscreen RawImage canvas and connects it to WebRtcVideoSession.
/// Video frames arrive as Texture from WebRTC and are assigned directly —
/// no JPEG decode, no polling loop.
///
/// Visibility mirrors the assembly-task state. Press V to toggle manually.
/// </summary>
public class ExpertVideoDisplay : MonoBehaviour
{
    private Canvas             canvas;
    private RawImage           videoImage;
    private ExperimentManager2 expManager;
    private WebRtcVideoSession session;
    private bool               _showWanted;   // does the current state (or V-toggle) want the video?

    // ── Init ────────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager2 manager)
    {
        expManager = manager;
        expManager.OnStateChanged += OnStateChanged;
        BuildUI();

        session = gameObject.AddComponent<WebRtcVideoSession>();
        session.StartAsAnswerer(OnFrameReceived);

        // Prime: Setup is set by direct assignment in ExperimentManager2.Initialize (not via
        // Transition()), so OnStateChanged never fires for it — without this the Setup video
        // would never show.
        OnStateChanged(expManager.CurrentState);

        FileLogger.Log("Transport", "[ExpertVideoDisplay] Initialized.");
    }

    public WebRtcVideoSession Session => session;

    // ── Frame callback (main thread, called by WebRTC) ───────────────────────

    private void OnFrameReceived(Texture tex)
    {
        videoImage.texture = tex;
        ApplyVisibility();   // a RawImage with a null texture renders a white quad — only show once a frame exists
    }

    // ── UI ──────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var go = new GameObject("WorkerVideoCanvas");
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5;
        go.AddComponent<CanvasScaler>();

        var imgGo = new GameObject("VideoImage");
        imgGo.transform.SetParent(go.transform, false);
        videoImage = imgGo.AddComponent<RawImage>();
        videoImage.color = Color.white;
        var fitter = imgGo.AddComponent<AspectRatioFitter>();
        fitter.aspectMode  = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = 4f / 3f;
        var rt = imgGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        go.SetActive(false);
    }

    // ── Update — keyboard toggle only ────────────────────────────────────────

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.vKey.wasPressedThisFrame)
        {
            _showWanted = !_showWanted;
            ApplyVisibility();
        }
    }

    // ── Experiment state ─────────────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        // During Setup, show the Worker's HMD camera so the operator can watch calibration / QR
        // placement. The Setup panel (sortingOrder 20) renders above this video canvas (5), so the
        // calib/QR status and approve button stay visible on top.
        _showWanted =
            state == ExperimentState.Setup
         || (state == ExperimentState.TaskRunning   && expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && expManager.CurrentStepType == StepType.Alignment);
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (canvas != null)
            canvas.gameObject.SetActive(_showWanted && videoImage != null && videoImage.texture != null);
    }

    private void OnDestroy()
    {
        if (expManager != null) expManager.OnStateChanged -= OnStateChanged;
        session?.Stop();
    }
}
