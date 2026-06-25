using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public class ExpertUI2 : MonoBehaviour
{
    [Header("Optional - assign a font with Japanese glyphs")]
    public Font japaneseFont;

    private void Awake()
    {
        if (japaneseFont != null) return;
        japaneseFont = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
        if (japaneseFont == null) japaneseFont = Resources.Load<Font>("Fonts/NotoSansCJK-Regular");
        if (japaneseFont == null) japaneseFont = Resources.Load<Font>("Fonts/NotoSansJP");
    }

    [Header("Behaviour")]
    [Tooltip("Automatically hide non-essential UI elements while a task is running.")]
    public bool hideNonEssentialDuringTask = true;

    // ── Zone A (top bar) ──────────────────────────────────────────────────
    private Canvas  canvas;
    private Text    headerText;       // ステップ N/M
    private Text    stateText;        // 状態ラベル（色付き）
    private Text    conditionLabel;   // 条件 N/10 — IR × Ray
    private Text    timerText;        // タイマー
    private Image   pythonDot;        // Python 状態インジケーター
    private Text    pythonDotLabel;   // Python: OK / NG
    private Image[] conditionDots = new Image[10];
    private Image   stateBand;        // 左端の状態色帯

    // ── Zone B (left panel) ───────────────────────────────────────────────
    private GameObject zoneBPanel;
    private Text       actionText;      // 状態別 短い行動ラベル（大）
    private Text       instructionText; // 詳細指示（HandleInstructionChanged が書く）
    private Text       hintText;        // キー操作ヒント

    // ── Zone D (bottom bar) ───────────────────────────────────────────────
    private Text bottomHintText;

    // ── Remote mesh visibility ────────────────────────────────────────────
    private MeshHandler _meshHandler;
    private bool        _meshVisible = false;

    // ── State ─────────────────────────────────────────────────────────────
    private bool manualHideOverride;

    [Header("Auto-hide")]
    [Tooltip("Seconds before zoneBPanel auto-hides during TaskRunning.")]
    public float autoHideTaskDelay   = 3f;
    [Tooltip("Seconds before zoneBPanel auto-hides during WhiteNoise.")]
    public float autoHideNoiseDelay  = 5f;
    [Tooltip("Seconds to re-show panel when a new instruction arrives mid-task.")]
    public float autoHideReshowDelay = 4f;

    private Coroutine _autoHideCoroutine;
    private bool      _inTaskState;

    // ── Python / OSC ──────────────────────────────────────────────────────
    private float  _lastPong    = -999f;
    private float  _nextPing    = 0f;
    private bool   _wasPythonOk = false;
    private const float PingInterval = 5f;
    private const float PongTimeout  = 8f;
    private OscSessionManager  _oscSession;
    private System.Action      _onPongHandler;
    private System.Action<int> _onCalibRetryHandler;

    private ExperimentManager2 manager;

    // ── Initialize ────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager2 experimentManager)
    {
        manager = experimentManager;
        BuildCanvas();

        manager.OnStateChanged       += HandleStateChanged;
        manager.OnTimerUpdated       += HandleTimerUpdated;
        manager.OnInstructionChanged += HandleInstructionChanged;
        manager.OnProgressChanged    += HandleProgressChanged;

        _oscSession = Object.FindAnyObjectByType<OscSessionManager>();
        if (_oscSession != null)
        {
            _onPongHandler = () => _lastPong = Time.time;
            _oscSession.OnPong += _onPongHandler;

            // キャリブ再試行: stateText を上書きせず instructionText に表示
            _onCalibRetryHandler = n =>
            {
                if (instructionText != null)
                    instructionText.text = $"キャリブレーション再試行中... ({n}回目)";
            };
            _oscSession.OnCalibrationRetrying += _onCalibRetryHandler;
        }

        HandleStateChanged(manager.CurrentState);
    }

    private void OnDestroy()
    {
        if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
        if (manager == null) return;
        manager.OnStateChanged       -= HandleStateChanged;
        manager.OnTimerUpdated       -= HandleTimerUpdated;
        manager.OnInstructionChanged -= HandleInstructionChanged;
        manager.OnProgressChanged    -= HandleProgressChanged;
        if (_oscSession != null)
        {
            if (_onPongHandler       != null) _oscSession.OnPong               -= _onPongHandler;
            if (_onCalibRetryHandler != null) _oscSession.OnCalibrationRetrying -= _onCalibRetryHandler;
        }
    }

    // ── Update ────────────────────────────────────────────────────────────

    private void Update()
    {
        if (manager == null) return;

        var kb = Keyboard.current;
        if (kb != null && kb.tabKey.wasPressedThisFrame)
        {
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
            // Toggle based on actual visibility — not manualHideOverride flag
            bool currentlyVisible = zoneBPanel != null && zoneBPanel.activeSelf;
            manualHideOverride = currentlyVisible; // visible → hide; hidden → clear override and show
            if (zoneBPanel != null) zoneBPanel.SetActive(!manualHideOverride);
        }

        // M key: toggle SharedMesh visibility on Worker's Quest for calibration verification.
        if (kb != null && kb.mKey.wasPressedThisFrame)
            ToggleRemoteMesh();

        if (_oscSession != null && Time.time >= _nextPing)
        {
            _nextPing = Time.time + PingInterval;
            _oscSession.Ping();
        }

        UpdatePythonStatus();
    }

    // ── Build ─────────────────────────────────────────────────────────────

    private void BuildCanvas()
    {
        if (canvas != null) { Destroy(canvas.gameObject); canvas = null; }

        var go = new GameObject("ExpertUI2Canvas");
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();

        BuildTopBar(go.transform);
        BuildLeftPanel(go.transform);
        BuildBottomBar(go.transform);
    }

    private void BuildTopBar(Transform root)
    {
        MakePanel(root, new Rect(0f, 0.93f, 1f, 0.07f), new Color(0.04f, 0.04f, 0.04f, 0.82f));

        // 左端状態色帯
        stateBand = MakePanel(root, new Rect(0f, 0.93f, 0.17f, 0.07f), new Color(0.10f, 0.08f, 0f, 0.85f));

        // Row 1 (anchorY 0.965–1.00)
        headerText = MakeText(root,
            new Vector2(0.01f, 0.965f), new Vector2(0.17f, 1.00f),
            CoGazeStrings.Expert2_HeaderDefault, 17, TextAnchor.MiddleLeft, new Color(0.55f, 0.60f, 0.65f));

        stateText = MakeText(root,
            new Vector2(0.17f, 0.965f), new Vector2(0.36f, 1.00f),
            CoGazeStrings.Expert2_StateDefault, 20, TextAnchor.MiddleLeft, new Color(1f, 0.85f, 0.2f));

        conditionLabel = MakeText(root,
            new Vector2(0.36f, 0.965f), new Vector2(0.70f, 1.00f),
            "", 18, TextAnchor.MiddleLeft, new Color(0.68f, 0.84f, 1.00f));

        timerText = MakeText(root,
            new Vector2(0.74f, 0.965f), new Vector2(0.88f, 1.00f),
            CoGazeStrings.Expert2_TimerBlank, 24, TextAnchor.MiddleRight, Color.white);

        // Python インジケーター（Image 丸 + テキスト）
        {
            var dgo = new GameObject("PythonDot");
            dgo.transform.SetParent(root, false);
            pythonDot = dgo.AddComponent<Image>();
            pythonDot.color = Color.gray;
            var rt = dgo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.89f, 0.970f);
            rt.anchorMax = new Vector2(0.921f, 0.999f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        pythonDotLabel = MakeText(root,
            new Vector2(0.925f, 0.965f), new Vector2(1.00f, 1.00f),
            CoGazeStrings.Expert2_PythonDefault, 14, TextAnchor.MiddleLeft, Color.gray);

        // Row 2 (anchorY 0.930–0.963): 条件進捗ドット 10個
        const float dotW   = 0.017f;
        const float dotGap = 0.005f;
        float blockW = dotW * 10 + dotGap * 9;
        float startX = (1f - blockW) / 2f;

        conditionDots = new Image[10];
        for (int i = 0; i < 10; i++)
        {
            var dgo = new GameObject($"CondDot_{i}");
            dgo.transform.SetParent(root, false);
            var img = dgo.AddComponent<Image>();
            img.color = new Color(0.25f, 0.28f, 0.32f);
            var rt = dgo.GetComponent<RectTransform>();
            float x0 = startX + i * (dotW + dotGap);
            rt.anchorMin = new Vector2(x0, 0.932f);
            rt.anchorMax = new Vector2(x0 + dotW, 0.963f);
            rt.offsetMin = new Vector2(2, 2);
            rt.offsetMax = new Vector2(-2, -2);
            conditionDots[i] = img;
        }
    }

    private void BuildLeftPanel(Transform root)
    {
        var panelGo = new GameObject("ZoneBPanel");
        panelGo.transform.SetParent(root, false);
        var bg = panelGo.AddComponent<Image>();
        bg.color = new Color(0.03f, 0.03f, 0.08f, 0.76f);
        var bgRt = panelGo.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 0.04f);
        bgRt.anchorMax = new Vector2(0.26f, 0.93f);
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
        zoneBPanel = panelGo;

        var pt = panelGo.transform;

        // actionText: パネル上部 44%（大きい状態ラベル）
        actionText = MakeText(pt,
            new Vector2(0.05f, 0.56f), new Vector2(0.95f, 0.98f),
            "", 26, TextAnchor.UpperLeft, Color.white);

        // instructionText: パネル中部 38%（詳細 / HandleInstructionChanged が書く）
        instructionText = MakeText(pt,
            new Vector2(0.05f, 0.18f), new Vector2(0.95f, 0.56f),
            "", 19, TextAnchor.UpperLeft, new Color(0.78f, 0.84f, 0.90f));

        // hintText: パネル下部 18%（キー操作ガイド）
        hintText = MakeText(pt,
            new Vector2(0.05f, 0.01f), new Vector2(0.95f, 0.18f),
            "", 16, TextAnchor.LowerLeft, new Color(0.48f, 0.54f, 0.60f));
    }

    private void BuildBottomBar(Transform root)
    {
        MakePanel(root, new Rect(0f, 0f, 1f, 0.04f), new Color(0.04f, 0.04f, 0.04f, 0.65f));
        bottomHintText = MakeText(root,
            new Vector2(0f, 0f), new Vector2(1f, 0.04f),
            CoGazeStrings.Expert2_BottomHint,
            15, TextAnchor.MiddleCenter, new Color(0.40f, 0.44f, 0.48f));
    }

    // ── State handler ─────────────────────────────────────────────────────
    // HandleStateChanged: stateText / actionText / hintText / timerText 初期値 を担当
    // HandleInstructionChanged: instructionText の唯一の書き込み権限
    // HandleProgressChanged: headerText + conditionDots のみ（instructionText は書かない）

    private void HandleStateChanged(ExperimentState state)
    {
        switch (state)
        {
            case ExperimentState.Idle:
                SetZoneA(MessageBank.Get("ui.idle.state"),
                    new Color(1.00f, 0.85f, 0.20f),
                    new Color(0.10f, 0.08f, 0.00f, 0.85f));
                SetZoneB(MessageBank.Get("ui.idle.action"),
                    MessageBank.Get("ui.idle.detail"),
                    MessageBank.Get("ui.idle.hint"));
                timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                timerText.color = Color.white;
                conditionLabel.text = "";
                break;

            case ExperimentState.Ready:
                SetZoneA(MessageBank.Get("ui.ready.state"),
                    new Color(0.15f, 0.90f, 0.40f),
                    new Color(0.00f, 0.14f, 0.05f, 0.85f));
                SetZoneB(MessageBank.Get("ui.ready.action"),
                    MessageBank.Get("ui.ready.detail"),
                    MessageBank.Get("ui.ready.hint"));
                timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                timerText.color = Color.white;
                break;

            case ExperimentState.WhiteNoise:
            {
                int ci     = manager != null ? manager.CurrentConditionIndex : -1;
                int runPos = manager != null ? manager.CurrentConditionRunPosition : -1;
                string num  = runPos >= 0 ? $"{runPos + 1}/{ExperimentDesign.Conditions.Length}" : "—";
                string name = ci >= 0 ? GetConditionDisplayName(ci) : "—";
                SetZoneA(MessageBank.Get("ui.noise.state"),
                    new Color(0.30f, 0.75f, 1.00f),
                    new Color(0.00f, 0.09f, 0.18f, 0.85f));
                conditionLabel.text = $"条件 {num} — {name}";
                SetZoneB(MessageBank.Get("ui.noise.action"),
                    MessageBank.Get("ui.noise.detail"),
                    MessageBank.Get("ui.noise.hint"));
                timerText.color = new Color(1f, 0.85f, 0f);
                UpdateConditionDots();
                break;
            }

            case ExperimentState.TaskRunning:
                SetZoneA(MessageBank.Get("ui.task.state"),
                    new Color(1.00f, 0.35f, 0.35f),
                    new Color(0.18f, 0.00f, 0.00f, 0.85f));
                if (actionText      != null) actionText.text      = MessageBank.Get("ui.task.state");
                if (instructionText != null) instructionText.text  = ""; // HandleInstructionChanged が上書き
                if (hintText        != null) hintText.text         = MessageBank.Get("ui.task.hint");
                timerText.color = Color.white;
                break;

            case ExperimentState.Questionnaire:
                timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                timerText.color = Color.white;
                if (manager != null && manager.CurrentStepType == StepType.ConditionStart)
                {
                    SetZoneA(MessageBank.Get("ui.condstart.state"),
                        new Color(0.20f, 1.00f, 0.55f),
                        new Color(0.00f, 0.14f, 0.06f, 0.85f));
                    bool needsCalib = manager.CurrentConditionType == ConditionType.Webcam
                                   || manager.CurrentConditionType == ConditionType.WebcamFiltered;
                    string hint = needsCalib
                        ? MessageBank.Get("ui.condstart.hint_calib")
                        : MessageBank.Get("ui.condstart.hint");
                    SetZoneB(MessageBank.Get("ui.condstart.action"), "", hint);
                }
                else if (manager != null && manager.CurrentStepType == StepType.Alignment)
                {
                    SetZoneA(MessageBank.Get("ui.alignment.state"),
                        new Color(0.80f, 0.60f, 1.00f),
                        new Color(0.08f, 0.04f, 0.16f, 0.85f));
                    SetZoneB(MessageBank.Get("ui.alignment.action"),
                        MessageBank.Get("ui.alignment.detail"),
                        MessageBank.Get("ui.alignment.hint"));
                    // auto-hide is applied in ApplyVisibility; no coroutine here
                }
                else
                {
                    SetZoneA(MessageBank.Get("ui.questionnaire.state"),
                        new Color(0.40f, 0.80f, 1.00f),
                        new Color(0.03f, 0.09f, 0.18f, 0.85f));
                    SetZoneB(MessageBank.Get("ui.questionnaire.action"),
                        MessageBank.Get("ui.questionnaire.detail"),
                        MessageBank.Get("ui.questionnaire.hint"));
                }
                break;

            case ExperimentState.TaskComplete:
                SetZoneA(MessageBank.Get("ui.taskcomplete.state"),
                    new Color(1.00f, 0.60f, 0.15f),
                    new Color(0.17f, 0.07f, 0.00f, 0.85f));
                SetZoneB(MessageBank.Get("ui.taskcomplete.action"),
                    MessageBank.Get("ui.taskcomplete.detail"),
                    MessageBank.Get("ui.taskcomplete.hint"));
                timerText.text  = CoGazeStrings.Expert2_TimerZero;
                timerText.color = Color.white;
                break;

            case ExperimentState.NoiseComplete:
                SetZoneA(MessageBank.Get("ui.noisecomplete.state"),
                    new Color(1.00f, 0.60f, 0.15f),
                    new Color(0.17f, 0.07f, 0.00f, 0.85f));
                SetZoneB(MessageBank.Get("ui.noisecomplete.action"),
                    MessageBank.Get("ui.noisecomplete.detail"),
                    MessageBank.Get("ui.noisecomplete.hint"));
                timerText.text  = CoGazeStrings.Expert2_TimerZero;
                timerText.color = Color.white;
                break;

            case ExperimentState.Finished:
            {
                string pid = manager != null ? manager.participantId : "—";
                SetZoneA(MessageBank.Get("ui.finished.state"),
                    new Color(0.55f, 0.55f, 0.55f),
                    new Color(0.07f, 0.07f, 0.07f, 0.85f));
                SetZoneB(MessageBank.Get("ui.finished.action"),
                    MessageBank.Format("ui.finished.detail", ("participantId", pid)),
                    "");
                timerText.text  = CoGazeStrings.Expert2_TimerZero;
                timerText.color = Color.white;
                UpdateConditionDots();
                break;
            }
        }

        ApplyVisibility(state);
    }

    private void SetZoneA(string label, Color textColor, Color bandColor)
    {
        if (stateText != null) { stateText.text = label; stateText.color = textColor; }
        if (stateBand != null) stateBand.color = bandColor;
    }

    private void SetZoneB(string action, string detail, string hint)
    {
        if (actionText      != null) actionText.text      = action;
        if (instructionText != null) instructionText.text  = detail;
        if (hintText        != null) hintText.text         = hint;
    }

    // ── Progress handler ──────────────────────────────────────────────────

    private void HandleProgressChanged(int stepIdx, int totalSteps, StepType stepType)
    {
        string total = totalSteps > 0 ? totalSteps.ToString() : "-";
        if (headerText != null)
            headerText.text = $"ステップ {stepIdx + 1}/{total}";

        if (stepType == StepType.ConditionStart)
        {
            int ci     = manager != null ? manager.CurrentConditionIndex : -1;
            int runPos = manager != null ? manager.CurrentConditionRunPosition : -1;
            UpdateConditionDots();
            if (ci >= 0 && conditionLabel != null)
                conditionLabel.text =
                    $"条件 {runPos + 1}/{ExperimentDesign.Conditions.Length} — {GetConditionDisplayName(ci)}";
        }
        // instructionText への書き込みなし（HandleInstructionChanged が唯一の書き込み元）
    }

    // ── Timer handler ─────────────────────────────────────────────────────

    private void HandleTimerUpdated(float remaining)
    {
        if (timerText == null || manager == null) return;
        timerText.text = FormatTime(remaining);
        if (manager.CurrentState == ExperimentState.WhiteNoise)
            timerText.color = new Color(1f, 0.85f, 0f);
        else
            timerText.color = remaining < 30f ? new Color(1f, 0.35f, 0.35f) : Color.white;
    }

    // ── Instruction handler ───────────────────────────────────────────────

    private void HandleInstructionChanged(string instruction)
    {
        if (string.IsNullOrEmpty(instruction) || instructionText == null) return;
        instructionText.text = instruction;

        // Mid-task: briefly re-show the panel so the Expert sees the new instruction
        if (_inTaskState && hideNonEssentialDuringTask && !manualHideOverride
            && zoneBPanel != null && !zoneBPanel.activeSelf)
        {
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
            zoneBPanel.SetActive(true);
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideReshowDelay));
        }
    }

    // ── Condition dots ────────────────────────────────────────────────────

    private void UpdateConditionDots()
    {
        if (manager == null) return;
        int runPos   = manager.CurrentConditionRunPosition;
        bool allDone = manager.CurrentState == ExperimentState.Finished;

        for (int i = 0; i < conditionDots.Length; i++)
        {
            if (conditionDots[i] == null) continue;
            if (allDone)
                conditionDots[i].color = new Color(0.15f, 0.85f, 0.35f); // 全完了: 緑
            else if (runPos < 0)
                conditionDots[i].color = new Color(0.25f, 0.28f, 0.32f); // 未開始: 暗グレー
            else if (i < runPos)
                conditionDots[i].color = new Color(0.15f, 0.85f, 0.35f); // 完了: 緑
            else if (i == runPos)
                conditionDots[i].color = new Color(1.00f, 0.82f, 0.00f); // 実行中: 黄
            else
                conditionDots[i].color = new Color(0.25f, 0.28f, 0.32f); // 未実施: 暗グレー
        }
    }

    // ── Python status ─────────────────────────────────────────────────────

    private void UpdatePythonStatus()
    {
        if (pythonDot == null) return;
        Color ok  = new Color(0.10f, 0.90f, 0.30f);
        Color ng  = new Color(1.00f, 0.20f, 0.20f);
        Color unk = new Color(1.00f, 0.85f, 0.00f);

        if (_oscSession == null)
        {
            pythonDot.color      = Color.gray;
            if (pythonDotLabel != null) { pythonDotLabel.text = CoGazeStrings.Expert2_PythonDefault; pythonDotLabel.color = Color.gray; }
            return;
        }
        if (_lastPong < 0f)
        {
            bool timeout = Time.time > PongTimeout;
            if (timeout && _wasPythonOk)
            {
                FileLogger.Log("Experiment", "[ExpertUI2] Python TIMEOUT (no pong received)");
                _wasPythonOk = false;
            }
            pythonDot.color = timeout ? ng : unk;
            if (pythonDotLabel != null)
            {
                pythonDotLabel.text  = timeout ? CoGazeStrings.Expert2_PythonNG : CoGazeStrings.Expert2_PythonWaiting;
                pythonDotLabel.color = timeout ? ng : unk;
            }
            return;
        }
        float age = Time.time - _lastPong;
        if (age < PongTimeout)
        {
            _wasPythonOk = true;
            pythonDot.color = ok;
            if (pythonDotLabel != null)
            {
                pythonDotLabel.text  = $"Python: OK ({age:F0}s)";
                pythonDotLabel.color = ok;
            }
        }
        else
        {
            if (_wasPythonOk)
            {
                FileLogger.Log("Experiment", $"[ExpertUI2] Python TIMEOUT, last pong {age:F1}s ago");
                _wasPythonOk = false;
            }
            pythonDot.color = ng;
            if (pythonDotLabel != null)
            {
                pythonDotLabel.text  = CoGazeStrings.Expert2_PythonNG;
                pythonDotLabel.color = ng;
            }
        }
    }

    // ── Visibility ────────────────────────────────────────────────────────

    private void ApplyVisibility(ExperimentState state)
    {
        if (zoneBPanel == null) return;
        if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }

        // Gate states always show the panel and clear manual hide
        bool isGateState = state == ExperimentState.Idle
                        || state == ExperimentState.Ready
                        || state == ExperimentState.TaskComplete
                        || state == ExperimentState.NoiseComplete
                        || state == ExperimentState.Questionnaire
                        || state == ExperimentState.Finished;
        if (isGateState) manualHideOverride = false;

        _inTaskState = false;
        if (hintText != null)
            hintText.gameObject.SetActive(state != ExperimentState.TaskRunning || !hideNonEssentialDuringTask);

        if (state == ExperimentState.TaskRunning && hideNonEssentialDuringTask)
        {
            _inTaskState = true;
            zoneBPanel.SetActive(true); // show instruction, then auto-hide
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideTaskDelay));
        }
        else if (state == ExperimentState.WhiteNoise && hideNonEssentialDuringTask)
        {
            zoneBPanel.SetActive(true); // show briefly then auto-hide
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideNoiseDelay));
        }
        else if (state == ExperimentState.Questionnaire
                 && manager != null
                 && manager.CurrentStepType == StepType.Alignment
                 && hideNonEssentialDuringTask)
        {
            zoneBPanel.SetActive(true);
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideTaskDelay));
        }
        else
        {
            zoneBPanel.SetActive(!manualHideOverride);
        }
    }

    private IEnumerator AutoHideAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (zoneBPanel != null) zoneBPanel.SetActive(false);
        _autoHideCoroutine = null;
    }

    // ── Remote mesh toggle ────────────────────────────────────────────────

    private void ToggleRemoteMesh()
    {
        // Lazy lookup: Worker must have joined and instantiated LocalWorker before MeshHandler exists.
        if (_meshHandler == null)
            _meshHandler = Object.FindAnyObjectByType<MeshHandler>();
        if (_meshHandler == null)
        {
            Debug.LogWarning("[ExpertUI2] ToggleRemoteMesh: MeshHandler not found — is Worker connected?");
            return;
        }
        _meshVisible = !_meshVisible;
        _meshHandler.RequestSetMeshVisible(_meshVisible);
        Debug.Log($"[ExpertUI2] Remote mesh → {(_meshVisible ? "ON" : "OFF")}");

        if (instructionText != null)
        {
            instructionText.text = $"[キャリブ確認] Workerメッシュ表示: {(_meshVisible ? "ON ▶ 映像で位置確認してください" : "OFF")}   (M キーで切替)";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string GetConditionDisplayName(int condIdx)
    {
        if (condIdx < 0 || condIdx >= ExperimentDesign.Conditions.Length) return "—";
        var c = ExperimentDesign.Conditions[condIdx];
        return c.noise == ConditionType.NoGaze ? "NoGaze" : $"{c.noise} × {c.gaze}";
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
        rt.anchorMin = new Vector2(anchorRect.x,    anchorRect.y);
        rt.anchorMax = new Vector2(anchorRect.xMax, anchorRect.yMax);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return img;
    }

    private Text MakeText(Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                          string defaultText, int fontSize, TextAnchor alignment, Color color)
    {
        defaultText ??= string.Empty;
        string clip = defaultText.Substring(0, Mathf.Min(12, defaultText.Length));
        var go   = new GameObject("Text_" + clip);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.text               = defaultText;
        text.fontSize           = fontSize;
        text.alignment          = alignment;
        text.color              = color;
        text.font               = japaneseFont != null ? japaneseFont : GetBuiltinFont();
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow   = VerticalWrapMode.Truncate;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return text;
    }

    private static Font GetBuiltinFont()
    {
        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }
}
