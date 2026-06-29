using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

// Expert (PC): fullscreen RawImage _canvas driven by WebRtcVideoSession; visibility mirrors assembly state, V to toggle.
public class ExpertVideoDisplay : MonoBehaviour
{
    private Canvas             _canvas;
    private RawImage           _videoImage;
    private ExperimentManager2 _expManager;
    private WebRtcVideoSession _session;
    private bool               _showWanted;   // does the current state (or V-toggle) want the video?

    // ── Init ────────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager2 manager)
    {
        _expManager = manager;
        _expManager.OnStateChanged += OnStateChanged;
        BuildUI();

        _session = gameObject.AddComponent<WebRtcVideoSession>();
        _session.StartAsAnswerer(OnFrameReceived);

        // Prime: Setup is set by direct assignment in ExperimentManager2.Initialize (not via
        // Transition()), so OnStateChanged never fires for it — without this the Setup video
        // would never show.
        OnStateChanged(_expManager.CurrentState);

        FileLogger.Log("Transport", "[ExpertVideoDisplay] Initialized.");
    }

    public WebRtcVideoSession Session => _session;

    // ── Frame callback (main thread, called by WebRTC) ───────────────────────

    private void OnFrameReceived(Texture tex)
    {
        _videoImage.texture = tex;
        ApplyVisibility();   // a RawImage with a null texture renders a white quad — only show once a frame exists
    }

    // ── UI ──────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var go = new GameObject("WorkerVideoCanvas");
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 5;
        go.AddComponent<CanvasScaler>();

        var imgGo = new GameObject("VideoImage");
        imgGo.transform.SetParent(go.transform, false);
        _videoImage = imgGo.AddComponent<RawImage>();
        _videoImage.color = Color.white;
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
        // placement. The Setup panel (sortingOrder 20) renders above this video _canvas (5), so the
        // calib/QR status and approve button stay visible on top.
        _showWanted =
            state == ExperimentState.Setup
         || (state == ExperimentState.TaskRunning   && _expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && _expManager.CurrentStepType == StepType.Alignment);
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_canvas != null)
            _canvas.gameObject.SetActive(_showWanted && _videoImage != null && _videoImage.texture != null);
    }

    private void OnDestroy()
    {
        if (_expManager != null) _expManager.OnStateChanged -= OnStateChanged;
        _session?.Stop();
    }
}
