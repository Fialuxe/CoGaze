using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Runs on the Remote Expert (PC) only — attached inside RemoteExpertSetup's IsMine block.
///
/// Receives JPEG frames from WorkerVideoStream via IVideoTransport and displays them
/// fullscreen behind the ExpertUI text overlay.
///
/// Visibility:
///   - Auto-shows when an assembly task begins, auto-hides when it ends.
///   - Press V at any time to manually toggle.
/// </summary>
public class ExpertVideoDisplay : MonoBehaviour
{
    private Canvas    canvas;
    private RawImage  videoImage;
    private Texture2D displayTex;
    private ExperimentManager expManager;
    private IVideoTransport   transport;

    // ── Init ──────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager manager, IVideoTransport videoTransport)
    {
        expManager = manager;
        transport  = videoTransport;
        expManager.OnStateChanged += OnStateChanged;
        BuildUI();
        Debug.Log("[ExpertVideoDisplay] Initialized.");
    }

    private void OnDestroy()
    {
        if (displayTex != null) Destroy(displayTex);
    }

    // ── UI Construction ───────────────────────────────────────────────

    private void BuildUI()
    {
        var canvasGo = new GameObject("WorkerVideoCanvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5; // below ExpertUI (sortingOrder=10) so text is on top

        canvasGo.AddComponent<CanvasScaler>();

        // Fullscreen video image
        var imgGo = new GameObject("VideoImage");
        imgGo.transform.SetParent(canvasGo.transform, false);
        videoImage = imgGo.AddComponent<RawImage>();
        videoImage.color = Color.white;
        var imgRt = imgGo.GetComponent<RectTransform>();
        imgRt.anchorMin = Vector2.zero;
        imgRt.anchorMax = Vector2.one;
        imgRt.offsetMin = Vector2.zero;
        imgRt.offsetMax = Vector2.zero;

        canvasGo.SetActive(false); // hidden until an assembly task starts
    }

    // ── Update — poll for frames + keyboard toggle ────────────────────

    private void Update()
    {
        // Keyboard toggle
        var kb = Keyboard.current;
        if (kb != null && kb.vKey.wasPressedThisFrame)
            canvas.gameObject.SetActive(!canvas.gameObject.activeSelf);

        // Poll for new frames
        if (transport != null && canvas.gameObject.activeSelf)
        {
            if (transport.TryDequeue(out byte[] jpeg))
            {
                if (displayTex == null) displayTex = new Texture2D(2, 2);

                if (ImageConversion.LoadImage(displayTex, jpeg, false))
                    videoImage.texture = displayTex;
            }
        }
    }

    // ── Experiment state ──────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        bool show =
            (state == ExperimentState.TaskRunning   && expManager.CurrentStepType == StepType.Assembly)
         || (state == ExperimentState.Questionnaire && expManager.CurrentStepType == StepType.Alignment);
        if (canvas != null) canvas.gameObject.SetActive(show);
    }
}
