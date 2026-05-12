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
/// Visibility mirrors the assembly-task state.  Press V to toggle manually.
/// </summary>
public class ExpertVideoDisplay : MonoBehaviour
{
    private Canvas             canvas;
    private RawImage           videoImage;
    private ExperimentManager  expManager;
    private WebRtcVideoSession session;

    // ── Init ────────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager manager)
    {
        expManager = manager;
        expManager.OnStateChanged += OnStateChanged;
        BuildUI();

        session = gameObject.AddComponent<WebRtcVideoSession>();
        session.StartAsAnswerer(OnFrameReceived);

        Debug.Log("[ExpertVideoDisplay] Initialized.");
    }

    public WebRtcVideoSession Session => session;

    // ── Frame callback (main thread, called by WebRTC) ───────────────────────

    private void OnFrameReceived(Texture tex)
    {
        videoImage.texture = tex;
        // canvas visibility is handled by OnStateChanged — tex keeps updating silently when hidden
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
            canvas.gameObject.SetActive(!canvas.gameObject.activeSelf);
    }

    // ── Experiment state ─────────────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        bool show =
            (state == ExperimentState.TaskRunning   && expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && expManager.CurrentStepType == StepType.Alignment);
        if (canvas != null) canvas.gameObject.SetActive(show);
    }

    private void OnDestroy()
    {
        session?.Stop();
    }
}
