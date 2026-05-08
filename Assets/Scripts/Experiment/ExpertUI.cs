using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Screen-space UI for the Remote Expert.
/// Creates the entire Canvas hierarchy in code — no Unity Editor objects needed.
/// Wire up by calling Initialize() and passing the ExperimentManager instance.
///
/// Layout — single top strip, rest of screen is unobstructed 3D view:
///   ┌──────────────────────────────────────────────────┐
///   │ Session 1/4  Task 1/2   │ ■ 実行中  │   02:45   │  ← row 1
///   │ [instruction text — left aligned]   │  [hint]   │  ← row 2
///   └──────────────────────────────────────────────────┘
///
/// During TaskRunning: row 2 and the state badge are hidden — only the timer remains.
/// Tab key toggles the full strip visibility at any time.
///
/// Font: assign a font with Japanese glyphs in the Inspector (or via Initialize).
/// If left null the system default font is used (ASCII only).
/// </summary>
public class ExpertUI : MonoBehaviour
{
    [Header("Optional — assign a font with Japanese glyphs")]
    public Font japaneseFont;

    [Header("Behaviour")]
    [Tooltip("Automatically hide non-essential UI elements while a task is running.")]
    public bool hideNonEssentialDuringTask = true;

    // UI elements
    private Canvas    canvas;
    private GameObject row1Go;      // full row-1 group
    private GameObject row2Go;      // full row-2 group (instruction + hint)
    private Text      headerText;
    private Text      instructionText;
    private Text      timerText;
    private Text      hintText;
    private Text      stateText;

    // State for Tab toggle
    private bool manualHideOverride = false;

    private ExperimentManager manager;

    // ── Init ──────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager experimentManager)
    {
        manager = experimentManager;
        BuildCanvas();

        manager.OnStateChanged       += HandleStateChanged;
        manager.OnTimerUpdated       += HandleTimerUpdated;
        manager.OnInstructionChanged += HandleInstructionChanged;
        manager.OnProgressChanged    += HandleProgressChanged;

        // Initial display
        HandleStateChanged(manager.CurrentState);
    }

    private void OnDestroy()
    {
        if (manager == null) return;
        manager.OnStateChanged       -= HandleStateChanged;
        manager.OnTimerUpdated       -= HandleTimerUpdated;
        manager.OnInstructionChanged -= HandleInstructionChanged;
        manager.OnProgressChanged    -= HandleProgressChanged;
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        // Tab key — manual toggle of the full strip
        if (kb.tabKey.wasPressedThisFrame)
        {
            manualHideOverride = !manualHideOverride;
            ApplyVisibility(manager.CurrentState);
        }
    }

    // ── Canvas Construction ───────────────────────────────────────────────

    private void BuildCanvas()
    {
        var go = new GameObject("ExpertUICanvas");

        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();

        // Single dark strip across the top — 3D view is fully clear below it
        MakePanel(go.transform, new Rect(0f, 0.86f, 1f, 0.14f), new Color(0f, 0f, 0f, 0.68f));

        // ── Row 1 group (step progress, state badge, timer) ────────────────
        row1Go = new GameObject("Row1");
        row1Go.transform.SetParent(go.transform, false);
        row1Go.AddComponent<RectTransform>();

        headerText = MakeText(go.transform, new Vector2(0.01f, 0.93f), new Vector2(0.52f, 1.00f),
            "ステップ  -/-", 24, TextAnchor.MiddleLeft, Color.cyan);

        stateText = MakeText(go.transform, new Vector2(0.52f, 0.93f), new Vector2(0.76f, 1.00f),
            "待機中", 22, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.2f));

        timerText = MakeText(go.transform, new Vector2(0.76f, 0.93f), new Vector2(0.99f, 1.00f),
            "--:--", 26, TextAnchor.MiddleRight, Color.white);

        // ── Row 2 group (instruction + hint) ──────────────────────────────
        row2Go = new GameObject("Row2");
        row2Go.transform.SetParent(go.transform, false);
        row2Go.AddComponent<RectTransform>();

        instructionText = MakeText(go.transform, new Vector2(0.01f, 0.86f), new Vector2(0.75f, 0.93f),
            "instructions.txt を読み込んでいます...", 26, TextAnchor.MiddleLeft, Color.white);
        instructionText.horizontalOverflow = HorizontalWrapMode.Wrap;
        instructionText.verticalOverflow   = VerticalWrapMode.Truncate;

        hintText = MakeText(go.transform, new Vector2(0.75f, 0.86f), new Vector2(0.99f, 0.93f),
            "[Enter] 開始", 18, TextAnchor.MiddleRight, new Color(0.6f, 0.6f, 0.6f));
    }

    // ── Event Handlers ────────────────────────────────────────────────────

    private void HandleStateChanged(ExperimentState state)
    {
        switch (state)
        {
            case ExperimentState.Idle:
                stateText.text       = "待機中";
                stateText.color      = new Color(1f, 0.85f, 0.2f);
                instructionText.text = "参加者の接続を待っています...";
                hintText.text        = string.Empty;
                timerText.text       = "--:--";
                timerText.color      = Color.white;
                break;

            case ExperimentState.Ready:
                stateText.text       = "準備完了";
                stateText.color      = Color.green;
                instructionText.text = "メッシュのキャリブレーションが終わっているかを確認し、[Enter] を押して実験を開始してください";
                hintText.text        = "[Enter] 開始";
                timerText.text       = "--:--";
                timerText.color      = Color.white;
                break;

            case ExperimentState.WhiteNoise:
                stateText.text       = "■ ノイズ再生中";
                stateText.color      = Color.yellow;
                instructionText.text = "ホワイトノイズ再生中...";
                hintText.text        = "[Del] スキップ";
                timerText.color      = Color.yellow;
                break;

            case ExperimentState.TaskRunning:
                stateText.text  = "タスク実行中です";
                stateText.color = Color.red;
                hintText.text   = "[Del] スキップ";
                timerText.color = Color.white;
                break;

            case ExperimentState.Questionnaire:
                stateText.text  = "アンケートに回答してください";
                stateText.color = new Color(0.4f, 0.8f, 1f);
                hintText.text   = "[Enter] 完了";
                timerText.text  = "--:--";
                timerText.color = Color.white;
                break;

            case ExperimentState.TaskComplete:
                stateText.text       = "タスク終了";
                stateText.color      = new Color(1f, 0.60f, 0.15f); // orange
                instructionText.text = "タスクが終了しました。アンケートへ回答し、回答が完了したら [Enter] を押してください。";
                hintText.text        = "[Enter] 次へ";
                timerText.text       = "00:00";
                timerText.color      = Color.white;
                break;

            case ExperimentState.NoiseComplete:
                stateText.text       = "ノイズ終了";
                stateText.color      = new Color(1f, 0.60f, 0.15f); // orange
                instructionText.text = "ホワイトノイズが終了しました。次のステップへ進む場合は [Enter] を押してください。";
                hintText.text        = "[Enter] 次へ";
                timerText.text       = "00:00";
                timerText.color      = Color.white;
                break;

            case ExperimentState.Finished:
                stateText.text       = "終了";
                stateText.color      = Color.gray;
                instructionText.text = "実験終了。ご協力ありがとうございました。";
                hintText.text        = string.Empty;
                timerText.text       = "00:00";
                timerText.color      = Color.white;
                break;
        }

        ApplyVisibility(state);
    }

    /// <summary>
    /// Control what is shown based on state and manual override.
    /// TaskRunning → timer only (row 2 + state badge hidden).
    /// All other states → full strip.
    /// Tab → toggles everything.
    /// </summary>
    private void ApplyVisibility(ExperimentState state)
    {
        if (canvas == null) return;

        if (manualHideOverride)
        {
            // Hide everything except the timer
            SetActive(stateText,       false);
            SetActive(headerText,      false);
            SetActive(instructionText, false);
            SetActive(hintText,        false);
            // Keep timer visible so Expert can still track time
            SetActive(timerText, true);
            return;
        }

        // During TaskRunning only: minimal view (timer + step count, no instruction/hint/badge)
        // All other states including TaskComplete get the full strip.
        bool taskRunning = hideNonEssentialDuringTask && state == ExperimentState.TaskRunning;

        SetActive(instructionText, true);          // always visible — Expert must see the task instruction
        SetActive(hintText,        !taskRunning);
        SetActive(stateText,       !taskRunning);

        // Always show these
        SetActive(headerText, true);
        SetActive(timerText,  true);
    }

    private void HandleTimerUpdated(float remaining)
    {
        timerText.text = FormatTime(remaining);
        if (manager.CurrentState == ExperimentState.WhiteNoise)
            timerText.color = Color.yellow;
        else
            timerText.color = remaining < 30f ? Color.red : Color.white;
    }

    private void HandleInstructionChanged(string instruction)
    {
        if (!string.IsNullOrEmpty(instruction))
            instructionText.text = instruction;
    }

    private void HandleProgressChanged(int stepIdx, int totalSteps, StepType stepType)
    {
        string total = totalSteps > 0 ? totalSteps.ToString() : "-";
        headerText.text = $"ステップ  {stepIdx + 1}/{total}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void SetActive(Behaviour b, bool active)
    {
        if (b != null) b.enabled = active;
    }

    private static string FormatTime(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        int m = (int)(seconds / 60f);
        int s = (int)(seconds % 60f);
        return $"{m:D2}:{s:D2}";
    }

    private Image MakePanel(Transform parent, Rect anchorRect, Color color)
    {
        var go  = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(anchorRect.x,     anchorRect.y);
        rt.anchorMax = new Vector2(anchorRect.xMax,  anchorRect.yMax);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return img;
    }

    private Text MakeText(Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                          string defaultText, int fontSize, TextAnchor alignment, Color color)
    {
        var go = new GameObject("Text_" + defaultText[..Mathf.Min(12, defaultText.Length)]);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();

        text.text      = defaultText;
        text.fontSize  = fontSize;
        text.alignment = alignment;
        text.color     = color;
        text.font      = japaneseFont != null ? japaneseFont : GetBuiltinFont();

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        return text;
    }

    private static Font GetBuiltinFont()
    {
        // Unity 2022+ uses "LegacyRuntime.ttf"; older versions use "Arial.ttf"
        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }
}
