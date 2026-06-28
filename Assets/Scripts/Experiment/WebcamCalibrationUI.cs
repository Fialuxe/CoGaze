using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Unity-driven 16-point webcam calibration overlay for the Expert PC screen.
///
/// Flow:
///   StartCalibration() →
///     /calibration/reset  (clear Python-side points)
///     [2 s instruction]
///     For each of 16 dots:
///       0.8 s travel (smooth lerp)
///       1.5 s dwell  → /calibration/sample [x, y] every 100 ms  (≈15 samples/dot)
///       0.4 s reward (dot shrinks)
///     /calibration/compute  (Python fits Ridge, sends /calibration/result)
///   OnCalibrationSequenceDone fired → caller waits for HandleCalibrationResult
///
/// Python's CalibrationManager receives 16 × ~15 = ~240 (local, target) pairs,
/// which gives the same accuracy as the old Python-window approach that averaged
/// ~30 samples per dot (Ridge handles redundant points correctly).
///
/// Coordinate convention: (0,0) = top-left of screen, (1,1) = bottom-right,
/// matching Python's calib_window.py TARGETS and screen pixel coordinate system.
/// </summary>
public class WebcamCalibrationUI : MonoBehaviour
{
    // ── Timing (serialised so operator can tweak without recompile) ───────────
    [Header("Timing (seconds)")]
    [SerializeField] private float travelSec  = 0.8f;
    [SerializeField] private float dwellSec   = 1.5f;
    [SerializeField] private float sampleSec  = 0.1f;   // interval between /calibration/sample sends
    [SerializeField] private float rewardSec  = 0.4f;

    // ── Visual ────────────────────────────────────────────────────────────────
    [Header("Visual")]
    [SerializeField] private float dotRadius  = 14f;    // px
    [SerializeField] private float ringRadius = 40f;    // px (start; converges to 0 during dwell)

    // ── Public ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired after /calibration/compute is sent (before /calibration/result arrives).
    /// Caller should wait for OscSessionManager.OnCalibrationResult.
    /// </summary>
    public event System.Action OnCalibrationSequenceDone;

    // ── 16-point grid ─────────────────────────────────────────────────────────
    // Matches Python calib_window.py TARGETS exactly (MARGIN = 0.05).
    private static readonly Vector2[] Targets =
    {
        new(0.05f, 0.05f), new(0.95f, 0.05f), new(0.05f, 0.95f), new(0.95f, 0.95f),
        new(0.50f, 0.05f), new(0.50f, 0.95f), new(0.05f, 0.50f), new(0.95f, 0.50f),
        new(0.25f, 0.25f), new(0.75f, 0.25f), new(0.25f, 0.75f), new(0.75f, 0.75f),
        new(0.25f, 0.50f), new(0.75f, 0.50f), new(0.50f, 0.25f), new(0.50f, 0.75f),
    };

    // ── Private state ─────────────────────────────────────────────────────────
    private OscSessionManager _oscSession;
    private GameObject        _canvasGo;
    private RectTransform     _dotRt;
    private RectTransform     _ringRt;
    private Image             _dotImg;
    private Image             _ringImg;
    private Text              _counterText;
    private Coroutine         _calibCo;

    private static readonly Color DotColorDwell  = Color.white;
    private static readonly Color DotColorTravel = new Color(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Color RingColor      = new Color(0.45f, 0.45f, 0.45f, 1f);

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        BuildUI();
    }

    private void Start()
    {
        _oscSession = FindAnyObjectByType<OscSessionManager>();
    }

    private void OnDestroy()
    {
        if (_calibCo != null) { StopCoroutine(_calibCo); _calibCo = null; }
        if (_canvasGo != null) Destroy(_canvasGo);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Start the 16-point calibration sequence. Safe to call during any state.</summary>
    public void StartCalibration()
    {
        if (_calibCo != null) StopCoroutine(_calibCo);
        if (_canvasGo != null) _canvasGo.SetActive(true);
        // Lazy-find in case Start() ran before OscSessionManager was instantiated
        if (_oscSession == null) _oscSession = FindAnyObjectByType<OscSessionManager>();
        _calibCo = StartCoroutine(RunSequence());
    }

    /// <summary>Abort a running calibration (e.g. operator presses ESC or Del).</summary>
    public void AbortCalibration()
    {
        if (_calibCo != null) { StopCoroutine(_calibCo); _calibCo = null; }
        if (_canvasGo != null) _canvasGo.SetActive(false);
    }

    // ── Calibration sequence ──────────────────────────────────────────────────

    private IEnumerator RunSequence()
    {
        _oscSession?.SendCalibrationReset();

        // Instruction overlay (2 s)
        _counterText.text =
            "目でドットを追ってください  /  Keep your head still.\n" +
            $"Starting in 2 s… ({Targets.Length} points)";
        MoveItems(NormToAnchor(new Vector2(0.5f, 0.5f)));
        _dotImg.color  = DotColorDwell;
        _ringImg.color = Color.clear;
        yield return new WaitForSeconds(2f);

        Vector2 curAnchor = NormToAnchor(new Vector2(0.5f, 0.5f));

        for (int i = 0; i < Targets.Length; i++)
        {
            _counterText.text = $"{i + 1} / {Targets.Length}";
            Vector2 toAnchor = NormToAnchor(Targets[i]);

            // ── TRAVEL ───────────────────────────────────────────────────────
            _dotImg.color  = DotColorTravel;
            _ringImg.color = Color.clear;
            for (float t = 0f; t < travelSec; t += Time.unscaledDeltaTime)
            {
                float f = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / travelSec));
                MoveItems(Vector2.Lerp(curAnchor, toAnchor, f));
                yield return null;
            }
            MoveItems(toAnchor);
            curAnchor = toAnchor;

            // ── DWELL ─────────────────────────────────────────────────────────
            _dotImg.color  = DotColorDwell;
            _ringImg.color = RingColor;
            SetRingSize(ringRadius);

            float dwellElapsed = 0f;
            float nextSample   = 0f;

            while (dwellElapsed < dwellSec)
            {
                dwellElapsed += Time.unscaledDeltaTime;
                float frac = Mathf.Clamp01(dwellElapsed / dwellSec);
                SetRingSize(Mathf.Lerp(ringRadius, 0f, frac));

                if (dwellElapsed >= nextSample)
                {
                    nextSample += sampleSec;
                    _oscSession?.SendCalibrationSample(Targets[i].x, Targets[i].y);
                }
                yield return null;
            }

            // ── REWARD ────────────────────────────────────────────────────────
            _ringImg.color = Color.clear;
            for (float t = 0f; t < rewardSec; t += Time.unscaledDeltaTime)
            {
                float shrink = 1f - Mathf.Clamp01(t / rewardSec);
                SetDotSize(dotRadius * shrink);
                yield return null;
            }
            SetDotSize(dotRadius);
        }

        // All dots done — compute
        _counterText.text = "キャリブレーション完了…";
        _oscSession?.SendCalibrationCompute();
        OnCalibrationSequenceDone?.Invoke();

        yield return new WaitForSeconds(0.8f);
        if (_canvasGo != null) _canvasGo.SetActive(false);
        _calibCo = null;
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        // Full-screen overlay canvas (ScreenSpaceOverlay, top sort order)
        _canvasGo = new GameObject("WebcamCalibOverlay");
        DontDestroyOnLoad(_canvasGo);  // survives scene reloads
        var canvas = _canvasGo.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 900;
        _canvasGo.AddComponent<CanvasScaler>();

        // Black background filling the entire canvas
        var bgRt = _canvasGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
        var bg = _canvasGo.AddComponent<Image>();
        bg.color = Color.black;

        // Ring (circle outline — converges during dwell)
        _ringRt = MakeCircleItem("Ring", _canvasGo.transform, ringRadius, false, RingColor);
        _ringImg = _ringRt.GetComponent<Image>();

        // Dot (filled circle — always visible during sequence)
        _dotRt  = MakeCircleItem("Dot", _canvasGo.transform, dotRadius, true, Color.white);
        _dotImg = _dotRt.GetComponent<Image>();

        // Counter label (top-left)
        var ctrGo = new GameObject("Counter");
        ctrGo.transform.SetParent(_canvasGo.transform, false);
        _counterText = ctrGo.AddComponent<Text>();
        _counterText.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _counterText.fontSize  = 22;
        _counterText.color     = new Color(0.6f, 0.6f, 0.6f, 1f);
        _counterText.alignment = TextAnchor.UpperLeft;
        var ctrRt = ctrGo.GetComponent<RectTransform>();
        ctrRt.anchorMin = Vector2.zero;
        ctrRt.anchorMax = Vector2.one;
        ctrRt.offsetMin = new Vector2(16f, 16f);
        ctrRt.offsetMax = Vector2.zero;

        _canvasGo.SetActive(false);
    }

    private RectTransform MakeCircleItem(
        string name, Transform parent, float radius, bool filled, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color  = color;
        img.sprite = MakeCircleSprite(Mathf.CeilToInt(radius), filled);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(radius * 2f, radius * 2f);
        return rt;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert normalised [0,1] target coords (y=0 → top, y=1 → bottom) to
    /// Canvas anchoredPosition relative to centre anchor (0.5, 0.5).
    /// </summary>
    private static Vector2 NormToAnchor(Vector2 norm)
    {
        float w = Screen.width;
        float h = Screen.height;
        return new Vector2((norm.x - 0.5f) * w, (0.5f - norm.y) * h);
    }

    private void MoveItems(Vector2 anchor)
    {
        _dotRt.anchoredPosition  = anchor;
        _ringRt.anchoredPosition = anchor;
    }

    private void SetRingSize(float r)
    {
        float d = Mathf.Max(r * 2f, 0f);
        _ringRt.sizeDelta = new Vector2(d, d);
    }

    private void SetDotSize(float r)
    {
        float d = Mathf.Max(r * 2f, 1f);
        _dotRt.sizeDelta = new Vector2(d, d);
    }

    private static Sprite MakeCircleSprite(int radius, bool filled)
    {
        int size = radius * 2 + 4;
        int cx   = size / 2;
        int cy   = size / 2;
        float r2 = (float)radius * radius;
        float i2 = filled ? 0f : (float)(radius - 2) * (radius - 2);

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - cx, dy = y - cy;
            float d2 = dx * dx + dy * dy;
            bool  on = filled ? (d2 <= r2) : (d2 <= r2 && d2 >= i2);
            pixels[y * size + x] = on
                ? new Color32(255, 255, 255, 255)
                : new Color32(0,   0,   0,   0  );
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}
