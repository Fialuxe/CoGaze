using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Photon.Pun;

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
    private Canvas  _canvas;
    private Text    _headerText;
    private Text    _stateText;
    private Text    _conditionLabel;
    private Text    _timerText;
    private Image   _pythonDot;
    private Text    _pythonDotLabel;
    private Image[] _conditionDots = new Image[10];
    private Image   _stateBand;

    // ── Zone B (left panel) ───────────────────────────────────────────────
    private GameObject _zoneBPanel;
    private Text       _actionText;
    private Text       _instructionText;
    private Text       _hintText;

    // ── Zone D (bottom bar) ───────────────────────────────────────────────
    private Text _bottomHintText;
    private Text _connStatusText;   // Photon 接続/相手在席（同室n/2・Worker接続・region/room）

    // ── Remote mesh visibility ────────────────────────────────────────────
    private MeshHandler _meshHandler;
    private bool        _meshVisible;

    // ── Identification task (live target + score) ─────────────────────────
    private IdentificationTask          _idTask;
    private System.Action<string, int>  _idTargetHandler;
    // Persistent panel (not inside _zoneBPanel; never auto-hides)
    private GameObject                  _idPanel;
    private Text                        _idTargetText;
    private Text                        _idScoreText;
    private Text                        _idQRListText;
    private string                      _idCurrentTarget;
    private System.Action<string, string, bool, int> _idAttemptHandler;
    private QRSpatialManager            _qrManager;
    private System.Action<string, Vector3, Quaternion> _qrDetectedHandler;
    private readonly System.Collections.Generic.HashSet<string> _detectedTaskQRIds = new();

    // ── Countdown overlay (3-2-1-GO) ─────────────────────────────────────
    private Text                        _countdownOverlay;
    private System.Action<int>          _countdownHandler;
    private Coroutine                   _countdownClearCo;

    // ── State ─────────────────────────────────────────────────────────────
    private bool _manualHideOverride;

    [Header("Auto-hide")]
    [Tooltip("Seconds before _zoneBPanel auto-hides during TaskRunning.")]
    public float autoHideTaskDelay   = 3f;
    [Tooltip("Seconds before _zoneBPanel auto-hides during WhiteNoise.")]
    public float autoHideNoiseDelay  = 5f;
    [Tooltip("Seconds to re-show panel when a new instruction arrives mid-task.")]
    public float autoHideReshowDelay = 4f;

    private Coroutine _autoHideCoroutine;
    private bool      _inTaskState;

    // ── Python / OSC ──────────────────────────────────────────────────────
    private float  _lastPong     = -999f;
    private float  _nextPing     = 0f;
    private float  _firstPingTime = -1f;   // set when the first ping is sent (CQ18: grace = elapsed since first ping)
    private bool   _wasPythonOk;
    private const float k_pingInterval = 5f;
    private const float k_pongTimeout  = 8f;
    private OscSessionManager  _oscSession;
    private System.Action      _onPongHandler;
    private System.Action<int> _onCalibRetryHandler;

    private ExperimentManager2 _manager;

    // ── Initialize ────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager2 experimentManager)
    {
        _manager = experimentManager;
        BuildCanvas();

        _manager.OnStateChanged       += HandleStateChanged;
        _manager.OnTimerUpdated       += HandleTimerUpdated;
        _manager.OnInstructionChanged += HandleInstructionChanged;
        _manager.OnProgressChanged    += HandleProgressChanged;

        // Identification task: show current target and live score in _instructionText.
        // Expert MUST know the target to point their gaze at the correct QR.
        _idTask = Object.FindAnyObjectByType<IdentificationTask>();
        if (_idTask != null)
        {
            _idTargetHandler = (targetId, score) =>
            {
                if (_manager.CurrentStepType != StepType.Task) return;
                _idCurrentTarget = targetId;
                int miss = _idTask?.MissCount ?? 0;
                if (_idTargetText != null)
                    _idTargetText.text = targetId != null ? $"ターゲット: {targetId}" : "識別終了";
                if (_idScoreText != null)
                    _idScoreText.text = targetId != null
                        ? $"正解数: {score}  ミス: {miss}"
                        : $"最終正解数: {score}  ミス: {miss}";
                RefreshIdQRList();
            };
            _idTask.OnTargetChanged += _idTargetHandler;

            // 誤クリック時はOnTargetChangedが発火しないので別途購読してミス数を更新
            _idAttemptHandler = (targetId, grippedId, correct, scoreAfter) =>
            {
                if (correct) return;
                if (_manager.CurrentStepType != StepType.Task) return;
                int miss = _idTask?.MissCount ?? 0;
                if (_idScoreText != null)
                    _idScoreText.text = $"正解数: {scoreAfter}  ミス: {miss}";
            };
            _idTask.OnIdentificationAttempt += _idAttemptHandler;
        }

        // QRマーカー一覧: 識別パネルに表示するため検出イベントを購読
        _qrManager = Object.FindAnyObjectByType<QRSpatialManager>();
        if (_qrManager != null)
        {
            foreach (var id in _qrManager.DetectedMarkers.Keys)
                if (IsTaskQRId(id)) _detectedTaskQRIds.Add(id);
            _qrDetectedHandler = (id, pos, rot) =>
            {
                if (!IsTaskQRId(id)) return;
                _detectedTaskQRIds.Add(id);
                RefreshIdQRList();
            };
            _qrManager.OnMarkerDetected += _qrDetectedHandler;
        }

        // 3-2-1-GO countdown: display each tick as a large overlay on the Expert screen.
        _countdownHandler = tick =>
        {
            if (_countdownOverlay == null) return;
            string[] labels = { "GO！", "1", "2", "3" };
            _countdownOverlay.text = (tick >= 0 && tick <= 3) ? labels[tick] : "";
            _countdownOverlay.gameObject.SetActive(true);
            if (_countdownClearCo != null) StopCoroutine(_countdownClearCo);
            float wait = tick == 0 ? 0.8f : 1.0f;
            _countdownClearCo = StartCoroutine(ClearCountdownAfter(wait));
        };
        _manager.OnCountdownTick += _countdownHandler;

        _oscSession = Object.FindAnyObjectByType<OscSessionManager>();
        if (_oscSession != null)
        {
            _onPongHandler = () => _lastPong = Time.time;
            _oscSession.OnPong += _onPongHandler;

            // キャリブ再試行: _stateText を上書きせず _instructionText に表示
            _onCalibRetryHandler = n =>
            {
                if (_instructionText != null)
                    _instructionText.text = $"キャリブレーション再試行中... ({n}回目)";
            };
            _oscSession.OnCalibrationRetrying += _onCalibRetryHandler;
        }

        HandleStateChanged(_manager.CurrentState);
    }

    private System.Collections.IEnumerator ClearCountdownAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_countdownOverlay != null) _countdownOverlay.gameObject.SetActive(false);
        _countdownClearCo = null;
    }

    private void OnDestroy()
    {
        if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
        if (_countdownClearCo  != null) { StopCoroutine(_countdownClearCo);  _countdownClearCo  = null; }
        if (_idTask     != null && _idTargetHandler   != null) _idTask.OnTargetChanged          -= _idTargetHandler;
        if (_idTask     != null && _idAttemptHandler  != null) _idTask.OnIdentificationAttempt  -= _idAttemptHandler;
        if (_qrManager  != null && _qrDetectedHandler != null) _qrManager.OnMarkerDetected      -= _qrDetectedHandler;
        if (_manager    != null && _countdownHandler  != null) _manager.OnCountdownTick      -= _countdownHandler;
        if (_manager == null) return;
        _manager.OnStateChanged       -= HandleStateChanged;
        _manager.OnTimerUpdated       -= HandleTimerUpdated;
        _manager.OnInstructionChanged -= HandleInstructionChanged;
        _manager.OnProgressChanged    -= HandleProgressChanged;
        if (_oscSession != null)
        {
            if (_onPongHandler       != null) _oscSession.OnPong               -= _onPongHandler;
            if (_onCalibRetryHandler != null) _oscSession.OnCalibrationRetrying -= _onCalibRetryHandler;
        }
    }

    // ── Update ────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_manager == null) return;

        var kb = Keyboard.current;
        if (kb != null && kb.tabKey.wasPressedThisFrame)
        {
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
            // Toggle based on actual visibility — not _manualHideOverride flag
            bool currentlyVisible = _zoneBPanel != null && _zoneBPanel.activeSelf;
            _manualHideOverride = currentlyVisible; // visible → hide; hidden → clear override and show
            if (_zoneBPanel != null) _zoneBPanel.SetActive(!_manualHideOverride);
        }

        // M key: toggle SharedMesh visibility on Worker's Quest for calibration verification.
        if (kb != null && kb.mKey.wasPressedThisFrame)
            ToggleRemoteMesh();

        if (_oscSession != null && Time.time >= _nextPing)
        {
            if (_firstPingTime < 0f) _firstPingTime = Time.time;
            _nextPing = Time.time + k_pingInterval;
            _oscSession.Ping();
        }

        UpdatePythonStatus();
        RefreshConnectionStatus();
    }

    // Photon 接続/相手在席を下バーに常時表示。別室(別region/room)・Worker未接続を即検知できる。
    // 読み取りのみ（ネットワーク書き込みなし）。
    private void RefreshConnectionStatus()
    {
        if (_connStatusText == null) return;

        if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom)
        {
            _connStatusText.text  = "● 未接続";
            _connStatusText.color = new Color(1f, 0.4f, 0.4f);
            return;
        }

        bool workerOnline = false;
        foreach (var p in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(p) == RoleManager.ROLE_WORKER) { workerOnline = true; break; }
        }

        int    count  = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
        string region = string.IsNullOrEmpty(PhotonNetwork.CloudRegion) ? "?" : PhotonNetwork.CloudRegion;
        string room   = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : "-";

        _connStatusText.text  = $"● 同室 {count}/2 ・ {(workerOnline ? "Worker接続" : "Worker未接続")} ・ {region}/{room}";
        _connStatusText.color = workerOnline ? new Color(0.45f, 0.85f, 0.55f) : new Color(1f, 0.8f, 0.3f);
    }

    // ── Build ─────────────────────────────────────────────────────────────

    private void BuildCanvas()
    {
        if (_canvas != null) { Destroy(_canvas.gameObject); _canvas = null; }

        var go = new GameObject("ExpertUI2Canvas");
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 10;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();

        BuildTopBar(go.transform);
        BuildLeftPanel(go.transform);
        BuildBottomBar(go.transform);
        BuildIdentificationPanel(go.transform);

        // Full-screen countdown overlay (3-2-1-GO!) — hidden until first countdown fires
        var cdGo = new GameObject("CountdownOverlay");
        cdGo.transform.SetParent(go.transform, false);
        _countdownOverlay = cdGo.AddComponent<Text>();
        _countdownOverlay.font      = japaneseFont ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _countdownOverlay.fontSize  = 280;
        _countdownOverlay.alignment = TextAnchor.MiddleCenter;
        _countdownOverlay.color     = new Color(1f, 0.85f, 0.1f);
        var cdRt = cdGo.GetComponent<RectTransform>();
        cdRt.anchorMin = new Vector2(0.25f, 0.25f);
        cdRt.anchorMax = new Vector2(0.75f, 0.75f);
        cdRt.offsetMin = cdRt.offsetMax = Vector2.zero;
        cdGo.SetActive(false);
    }

    private void BuildTopBar(Transform root)
    {
        MakePanel(root, new Rect(0f, 0.93f, 1f, 0.07f), new Color(0.04f, 0.04f, 0.04f, 0.82f));

        // 左端状態色帯
        _stateBand = MakePanel(root, new Rect(0f, 0.93f, 0.17f, 0.07f), new Color(0.10f, 0.08f, 0f, 0.85f));

        // Row 1 (anchorY 0.965–1.00)
        _headerText = MakeText(root,
            new Vector2(0.01f, 0.965f), new Vector2(0.17f, 1.00f),
            CoGazeStrings.Expert2_HeaderDefault, 17, TextAnchor.MiddleLeft, new Color(0.55f, 0.60f, 0.65f));

        _stateText = MakeText(root,
            new Vector2(0.17f, 0.965f), new Vector2(0.36f, 1.00f),
            CoGazeStrings.Expert2_StateDefault, 20, TextAnchor.MiddleLeft, new Color(1f, 0.85f, 0.2f));

        _conditionLabel = MakeText(root,
            new Vector2(0.36f, 0.965f), new Vector2(0.70f, 1.00f),
            "", 18, TextAnchor.MiddleLeft, new Color(0.68f, 0.84f, 1.00f));

        _timerText = MakeText(root,
            new Vector2(0.74f, 0.965f), new Vector2(0.88f, 1.00f),
            CoGazeStrings.Expert2_TimerBlank, 24, TextAnchor.MiddleRight, Color.white);

        // Python インジケーター（Image 丸 + テキスト）
        {
            var dgo = new GameObject("PythonDot");
            dgo.transform.SetParent(root, false);
            _pythonDot = dgo.AddComponent<Image>();
            _pythonDot.color = Color.gray;
            var rt = dgo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.89f, 0.970f);
            rt.anchorMax = new Vector2(0.921f, 0.999f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        _pythonDotLabel = MakeText(root,
            new Vector2(0.925f, 0.965f), new Vector2(1.00f, 1.00f),
            CoGazeStrings.Expert2_PythonDefault, 14, TextAnchor.MiddleLeft, Color.gray);

        // Row 2 (anchorY 0.930–0.963): 条件進捗ドット 10個
        const float dotW   = 0.017f;
        const float dotGap = 0.005f;
        float blockW = dotW * 10 + dotGap * 9;
        float startX = (1f - blockW) / 2f;

        _conditionDots = new Image[10];
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
            _conditionDots[i] = img;
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
        _zoneBPanel = panelGo;

        var pt = panelGo.transform;

        // _actionText: パネル上部 44%（大きい状態ラベル）
        _actionText = MakeText(pt,
            new Vector2(0.05f, 0.56f), new Vector2(0.95f, 0.98f),
            "", 26, TextAnchor.UpperLeft, Color.white);

        // _instructionText: パネル中部 38%（詳細 / HandleInstructionChanged が書く）
        _instructionText = MakeText(pt,
            new Vector2(0.05f, 0.18f), new Vector2(0.95f, 0.56f),
            "", 19, TextAnchor.UpperLeft, new Color(0.78f, 0.84f, 0.90f));

        _hintText = MakeText(pt,
            new Vector2(0.05f, 0.01f), new Vector2(0.95f, 0.18f),
            "", 16, TextAnchor.LowerLeft, new Color(0.48f, 0.54f, 0.60f));
    }

    private void BuildBottomBar(Transform root)
    {
        MakePanel(root, new Rect(0f, 0f, 1f, 0.04f), new Color(0.04f, 0.04f, 0.04f, 0.65f));
        _bottomHintText = MakeText(root,
            new Vector2(0f, 0f), new Vector2(1f, 0.04f),
            CoGazeStrings.Expert2_BottomHint,
            15, TextAnchor.MiddleCenter, new Color(0.40f, 0.44f, 0.48f));

        // 接続/相手在席（下バー左寄せ）— 別室・別room・Worker未接続を一目で検知
        _connStatusText = MakeText(root,
            new Vector2(0.01f, 0f), new Vector2(0.46f, 0.04f),
            "● 接続確認中…", 14, TextAnchor.MiddleLeft, new Color(0.5f, 0.55f, 0.6f));
    }

    // 識別タスク専用パネル（自動非表示なし・_zoneBPanel の外に配置）
    private void BuildIdentificationPanel(Transform root)
    {
        var panelGo = new GameObject("IdentificationPanel");
        panelGo.transform.SetParent(root, false);
        var bg = panelGo.AddComponent<Image>();
        bg.color = new Color(0.10f, 0.03f, 0.03f, 0.88f);
        var rt = panelGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.27f, 0.63f);
        rt.anchorMax = new Vector2(0.72f, 0.92f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        _idPanel = panelGo;

        var pt = panelGo.transform;

        // 現在のターゲット（大）
        _idTargetText = MakeText(pt,
            new Vector2(0.05f, 0.45f), new Vector2(0.95f, 0.95f),
            "ターゲット: —", 40, TextAnchor.MiddleCenter, new Color(1.00f, 0.82f, 0.20f));

        // スコア（中）
        _idScoreText = MakeText(pt,
            new Vector2(0.05f, 0.22f), new Vector2(0.95f, 0.45f),
            "正解数: —", 22, TextAnchor.MiddleCenter, new Color(0.85f, 0.90f, 0.85f));

        // QR一覧（小）— 現在のターゲットを [►X] でハイライト
        _idQRListText = MakeText(pt,
            new Vector2(0.03f, 0.02f), new Vector2(0.97f, 0.22f),
            "QR: 検出中...", 16, TextAnchor.MiddleCenter, new Color(0.55f, 0.62f, 0.70f));

        panelGo.SetActive(false);
    }

    // ── State handler ─────────────────────────────────────────────────────
    // HandleStateChanged: _stateText / _actionText / hintText / _timerText 初期値 を担当
    // HandleInstructionChanged: _instructionText の唯一の書き込み権限
    // HandleProgressChanged: _headerText + _conditionDots のみ（_instructionText は書かない）

    private void HandleStateChanged(ExperimentState state)
    {
        switch (state)
        {
            case ExperimentState.Setup:
                SetZoneA("セットアップ", new Color(0.50f, 0.80f, 1.00f),
                         new Color(0f, 0.06f, 0.12f, 0.85f));
                SetZoneB("Worker のセットアップ進行中",
                         "Worker が QR キャリブと\nタスクマーカーを確認しています",
                         "[Tab] パネル表示切替");
                _timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                _timerText.color = Color.white;
                _conditionLabel.text = "";
                break;

            case ExperimentState.Tutorial:
                SetZoneA("チュートリアル",
                    new Color(0.50f, 0.85f, 1.00f),
                    new Color(0.00f, 0.07f, 0.16f, 0.85f));
                SetZoneB("Worker が画面の指示で操作を練習しています",
                    "チュートリアルは Worker の HUD 上で自動進行します（説明不要）。\n" +
                    "完了すると「✓ チュートリアル完了」と表示されます。\n" +
                    "補足があれば音声で伝え、Enter で実験開始へ進んでください。",
                    "[Enter] チュートリアル完了");
                _timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                _timerText.color = Color.white;
                _conditionLabel.text = "";
                break;

            case ExperimentState.Idle:
                SetZoneA(MessageBank.Get("ui.idle.state"),
                    new Color(1.00f, 0.85f, 0.20f),
                    new Color(0.10f, 0.08f, 0.00f, 0.85f));
                SetZoneB(MessageBank.Get("ui.idle.action"),
                    MessageBank.Get("ui.idle.detail"),
                    MessageBank.Get("ui.idle.hint"));
                _timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                _timerText.color = Color.white;
                _conditionLabel.text = "";
                break;

            case ExperimentState.Ready:
                SetZoneA(MessageBank.Get("ui.ready.state"),
                    new Color(0.15f, 0.90f, 0.40f),
                    new Color(0.00f, 0.14f, 0.05f, 0.85f));
                SetZoneB(MessageBank.Get("ui.ready.action"),
                    MessageBank.Get("ui.ready.detail"),
                    MessageBank.Get("ui.ready.hint"));
                _timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                _timerText.color = Color.white;
                break;

            case ExperimentState.WhiteNoise:
            {
                int ci     = _manager != null ? _manager.CurrentConditionIndex : -1;
                int runPos = _manager != null ? _manager.CurrentConditionRunPosition : -1;
                string num  = runPos >= 0 ? $"{runPos + 1}/{ExperimentDesign.Conditions.Length}" : "—";
                string name = ci >= 0 ? GetConditionDisplayName(ci) : "—";
                SetZoneA(MessageBank.Get("ui.noise.state"),
                    new Color(0.30f, 0.75f, 1.00f),
                    new Color(0.00f, 0.09f, 0.18f, 0.85f));
                _conditionLabel.text = $"条件 {num} — {name}";
                SetZoneB(MessageBank.Get("ui.noise.action"),
                    MessageBank.Get("ui.noise.detail"),
                    MessageBank.Get("ui.noise.hint"));
                _timerText.color = new Color(1f, 0.85f, 0f);
                UpdateConditionDots();
                break;
            }

            case ExperimentState.TaskRunning:
                SetZoneA(MessageBank.Get("ui.task.state"),
                    new Color(1.00f, 0.35f, 0.35f),
                    new Color(0.18f, 0.00f, 0.00f, 0.85f));
                if (_actionText      != null) _actionText.text      = MessageBank.Get("ui.task.state");
                if (_instructionText != null) _instructionText.text  = ""; // HandleInstructionChanged が上書き
                if (_hintText       != null) _hintText.text        = MessageBank.Get("ui.task.hint");
                _timerText.color = Color.white;
                break;

            case ExperimentState.Questionnaire:
                _timerText.text  = CoGazeStrings.Expert2_TimerBlank;
                _timerText.color = Color.white;
                if (_manager != null && _manager.CurrentStepType == StepType.ConditionStart)
                {
                    SetZoneA(MessageBank.Get("ui.condstart.state"),
                        new Color(0.20f, 1.00f, 0.55f),
                        new Color(0.00f, 0.14f, 0.06f, 0.85f));
                    bool needsCalib = _manager.CurrentConditionType == ConditionType.Webcam
                                   || _manager.CurrentConditionType == ConditionType.WebcamFiltered;
                    string hint = needsCalib
                        ? MessageBank.Get("ui.condstart.hint_calib")
                        : MessageBank.Get("ui.condstart.hint");
                    SetZoneB(MessageBank.Get("ui.condstart.action"), "", hint);
                }
                else if (_manager != null && _manager.CurrentStepType == StepType.Alignment)
                {
                    SetZoneA(MessageBank.Get("ui.alignment.state"),
                        new Color(0.80f, 0.60f, 1.00f),
                        new Color(0.08f, 0.04f, 0.16f, 0.85f));
                    SetZoneB(MessageBank.Get("ui.alignment.action"),
                        MessageBank.Get("ui.alignment.detail"),
                        MessageBank.Get("ui.alignment.hint"));
                    // auto-hide is applied in ApplyVisibility; no coroutine here
                }
                else if (_manager != null && _manager.CurrentStepType == StepType.Rest)
                {
                    // UX11: Rest reuses the Questionnaire gate, but the top-level label must read
                    // 休憩中, not アンケート中. The durable surfaces are _stateText / _actionText / hintText —
                    // HandleInstructionChanged later overwrites _instructionText with Rest_Expert, so we
                    // pass that same string as the detail to avoid a flicker.
                    SetZoneA(CoGazeStrings.Expert2_RestState,
                        new Color(0.45f, 0.85f, 0.80f),
                        new Color(0.00f, 0.12f, 0.11f, 0.85f));
                    SetZoneB(CoGazeStrings.Expert2_RestState,
                        CoGazeStrings.Rest_Expert,
                        CoGazeStrings.Expert2_RestHint);
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
            {
                SetZoneA(MessageBank.Get("ui.taskcomplete.state"),
                    new Color(1.00f, 0.60f, 0.15f),
                    new Color(0.17f, 0.07f, 0.00f, 0.85f));
                // UX12: only Assembly is followed by a questionnaire. After the identification task
                // (StepType.Task) there is no questionnaire, so don't point the operator at one.
                // CurrentStepType still holds the just-finished step here (TaskComplete is only reached
                // from TaskRunning, which is only Task or Assembly).
                string tcDetail = (_manager != null && _manager.CurrentStepType == StepType.Task)
                    ? CoGazeStrings.Expert2_TaskCompleteDetail_Identify
                    : MessageBank.Get("ui.taskcomplete.detail");
                SetZoneB(MessageBank.Get("ui.taskcomplete.action"),
                    tcDetail,
                    MessageBank.Get("ui.taskcomplete.hint"));
                _timerText.text  = CoGazeStrings.Expert2_TimerZero;
                _timerText.color = Color.white;
                break;
            }

            case ExperimentState.NoiseComplete:
                SetZoneA(MessageBank.Get("ui.noisecomplete.state"),
                    new Color(1.00f, 0.60f, 0.15f),
                    new Color(0.17f, 0.07f, 0.00f, 0.85f));
                SetZoneB(MessageBank.Get("ui.noisecomplete.action"),
                    MessageBank.Get("ui.noisecomplete.detail"),
                    MessageBank.Get("ui.noisecomplete.hint"));
                _timerText.text  = CoGazeStrings.Expert2_TimerZero;
                _timerText.color = Color.white;
                break;

            case ExperimentState.Finished:
            {
                string pid = _manager != null ? _manager.participantId : "—";
                SetZoneA(MessageBank.Get("ui.finished.state"),
                    new Color(0.55f, 0.55f, 0.55f),
                    new Color(0.07f, 0.07f, 0.07f, 0.85f));
                SetZoneB(MessageBank.Get("ui.finished.action"),
                    MessageBank.Format("ui.finished.detail", ("participantId", pid)),
                    "");
                _timerText.text  = CoGazeStrings.Expert2_TimerZero;
                _timerText.color = Color.white;
                UpdateConditionDots();
                break;
            }
        }

        UpdateBottomHint(state);
        ApplyVisibility(state);
    }

    // UX13: the bottom bar (Zone D) is always visible — even while the left panel auto-hides during
    // tasks — so it is the right place to make the otherwise-undiscoverable [M] mesh toggle,
    // [R] calib-retry, and the Setup approval button discoverable. Select the hint per state.
    private void UpdateBottomHint(ExperimentState state)
    {
        if (_bottomHintText == null) return;

        string hint;
        switch (state)
        {
            case ExperimentState.Setup:
                hint = CoGazeStrings.Expert2_HintSetup;
                break;
            case ExperimentState.Tutorial:
                hint = "[Enter] チュートリアル完了・実験準備へ進む";
                break;
            case ExperimentState.Ready:
                hint = CoGazeStrings.Expert2_HintReady;
                break;
            case ExperimentState.TaskRunning:
            case ExperimentState.WhiteNoise:
                hint = CoGazeStrings.Expert2_HintTask;
                break;
            case ExperimentState.Questionnaire:
                bool calibGate = _manager != null
                    && _manager.CurrentStepType == StepType.ConditionStart
                    && (_manager.CurrentConditionType == ConditionType.Webcam
                     || _manager.CurrentConditionType == ConditionType.WebcamFiltered);
                if (calibGate)
                    hint = CoGazeStrings.Expert2_HintCalibGate;       // [R] retry is meaningful here
                else if (_manager != null && _manager.CurrentStepType == StepType.Rest)
                    hint = CoGazeStrings.Expert2_HintRest;
                else
                    hint = CoGazeStrings.Expert2_HintGate;
                break;
            case ExperimentState.TaskComplete:
            case ExperimentState.NoiseComplete:
                hint = CoGazeStrings.Expert2_HintGate;
                break;
            default:
                hint = CoGazeStrings.Expert2_BottomHint;             // Idle / Finished / fallback
                break;
        }
        _bottomHintText.text = hint;
    }

    private void SetZoneA(string label, Color textColor, Color bandColor)
    {
        if (_stateText != null) { _stateText.text = label; _stateText.color = textColor; }
        if (_stateBand != null) _stateBand.color = bandColor;
    }

    private void SetZoneB(string action, string detail, string hint)
    {
        if (_actionText      != null) _actionText.text      = action;
        if (_instructionText != null) _instructionText.text  = detail;
        if (_hintText       != null) _hintText.text        = hint;
    }

    // ── Progress handler ──────────────────────────────────────────────────

    private void HandleProgressChanged(int stepIdx, int totalSteps, StepType stepType)
    {
        string total = totalSteps > 0 ? totalSteps.ToString() : "-";
        if (_headerText != null)
            _headerText.text = $"ステップ {stepIdx + 1}/{total}";

        if (stepType == StepType.ConditionStart)
        {
            int ci     = _manager != null ? _manager.CurrentConditionIndex : -1;
            int runPos = _manager != null ? _manager.CurrentConditionRunPosition : -1;
            UpdateConditionDots();
            if (ci >= 0 && _conditionLabel != null)
                _conditionLabel.text =
                    $"条件 {runPos + 1}/{ExperimentDesign.Conditions.Length} — {GetConditionDisplayName(ci)}";
        }
        // _instructionText への書き込みなし（HandleInstructionChanged が唯一の書き込み元）
    }

    // ── Timer handler ─────────────────────────────────────────────────────

    private void HandleTimerUpdated(float remaining)
    {
        if (_timerText == null || _manager == null) return;
        _timerText.text = FormatTime(remaining);
        if (_manager.CurrentState == ExperimentState.WhiteNoise)
            _timerText.color = new Color(1f, 0.85f, 0f);
        else
            _timerText.color = remaining < 30f ? new Color(1f, 0.35f, 0.35f) : Color.white;
    }

    // ── Instruction handler ───────────────────────────────────────────────

    private void HandleInstructionChanged(string instruction)
    {
        if (string.IsNullOrEmpty(instruction) || _instructionText == null) return;
        _instructionText.text = instruction;

        // Mid-task: briefly re-show the panel so the Expert sees the new instruction
        if (_inTaskState && hideNonEssentialDuringTask && !_manualHideOverride
            && _zoneBPanel != null && !_zoneBPanel.activeSelf)
        {
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
            _zoneBPanel.SetActive(true);
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideReshowDelay));
        }
    }

    // ── Condition dots ────────────────────────────────────────────────────

    private void UpdateConditionDots()
    {
        if (_manager == null) return;
        int runPos   = _manager.CurrentConditionRunPosition;
        bool allDone = _manager.CurrentState == ExperimentState.Finished;

        for (int i = 0; i < _conditionDots.Length; i++)
        {
            if (_conditionDots[i] == null) continue;
            if (allDone)
                _conditionDots[i].color = new Color(0.15f, 0.85f, 0.35f); // 全完了: 緑
            else if (runPos < 0)
                _conditionDots[i].color = new Color(0.25f, 0.28f, 0.32f); // 未開始: 暗グレー
            else if (i < runPos)
                _conditionDots[i].color = new Color(0.15f, 0.85f, 0.35f); // 完了: 緑
            else if (i == runPos)
                _conditionDots[i].color = new Color(1.00f, 0.82f, 0.00f); // 実行中: 黄
            else
                _conditionDots[i].color = new Color(0.25f, 0.28f, 0.32f); // 未実施: 暗グレー
        }
    }

    // ── Python status ─────────────────────────────────────────────────────

    private void UpdatePythonStatus()
    {
        if (_pythonDot == null) return;
        Color ok  = new Color(0.10f, 0.90f, 0.30f);
        Color ng  = new Color(1.00f, 0.20f, 0.20f);
        Color unk = new Color(1.00f, 0.85f, 0.00f);

        if (_oscSession == null)
        {
            _pythonDot.color      = Color.gray;
            if (_pythonDotLabel != null) { _pythonDotLabel.text = CoGazeStrings.Expert2_PythonDefault; _pythonDotLabel.color = Color.gray; }
            return;
        }
        if (_lastPong < 0f)
        {
            // CQ18: base the grace window on elapsed time since the FIRST ping, not absolute
            // Time.time. If pinging hasn't started yet (or the UI initialized late), stay in the
            // "waiting" state instead of falsely flipping to NG the instant the scene passes 8 s.
            bool timeout = _firstPingTime >= 0f && (Time.time - _firstPingTime) > k_pongTimeout;
            if (timeout && _wasPythonOk)
            {
                FileLogger.Log("Experiment", "[ExpertUI2] Python TIMEOUT (no pong received)");
                _wasPythonOk = false;
            }
            _pythonDot.color = timeout ? ng : unk;
            if (_pythonDotLabel != null)
            {
                _pythonDotLabel.text  = timeout ? CoGazeStrings.Expert2_PythonNG : CoGazeStrings.Expert2_PythonWaiting;
                _pythonDotLabel.color = timeout ? ng : unk;
            }
            return;
        }
        float age = Time.time - _lastPong;
        if (age < k_pongTimeout)
        {
            _wasPythonOk = true;
            _pythonDot.color = ok;
            if (_pythonDotLabel != null)
            {
                _pythonDotLabel.text  = $"Python: OK ({age:F0}s)";
                _pythonDotLabel.color = ok;
            }
        }
        else
        {
            if (_wasPythonOk)
            {
                FileLogger.Log("Experiment", $"[ExpertUI2] Python TIMEOUT, last pong {age:F1}s ago");
                _wasPythonOk = false;
            }
            _pythonDot.color = ng;
            if (_pythonDotLabel != null)
            {
                _pythonDotLabel.text  = CoGazeStrings.Expert2_PythonNG;
                _pythonDotLabel.color = ng;
            }
        }
    }

    // ── Visibility ────────────────────────────────────────────────────────

    private void ApplyVisibility(ExperimentState state)
    {
        if (_zoneBPanel == null) return;
        if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }

        // Gate states always show the panel and clear manual hide
        bool isGateState = state == ExperimentState.Idle
                        || state == ExperimentState.Ready
                        || state == ExperimentState.Tutorial
                        || state == ExperimentState.TaskComplete
                        || state == ExperimentState.NoiseComplete
                        || state == ExperimentState.Questionnaire
                        || state == ExperimentState.Finished;
        if (isGateState) _manualHideOverride = false;

        _inTaskState = false;
        if (_hintText != null)
            _hintText.gameObject.SetActive(state != ExperimentState.TaskRunning || !hideNonEssentialDuringTask);

        if (state == ExperimentState.TaskRunning && hideNonEssentialDuringTask)
        {
            _inTaskState = true;
            _zoneBPanel.SetActive(true); // show instruction, then auto-hide
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideTaskDelay));
        }
        else if (state == ExperimentState.WhiteNoise && hideNonEssentialDuringTask)
        {
            _zoneBPanel.SetActive(true); // show briefly then auto-hide
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideNoiseDelay));
        }
        else if (state == ExperimentState.Questionnaire
                 && _manager != null
                 && _manager.CurrentStepType == StepType.Alignment
                 && hideNonEssentialDuringTask)
        {
            _zoneBPanel.SetActive(true);
            _autoHideCoroutine = StartCoroutine(AutoHideAfter(autoHideTaskDelay));
        }
        else
        {
            _zoneBPanel.SetActive(!_manualHideOverride);
        }

        // 識別パネルは自動非表示なし。識別タスクステップ中のみ表示
        if (_idPanel != null)
        {
            bool isIdTaskStep = _idTask != null && _manager != null
                             && _manager.CurrentStepType == StepType.Task;
            _idPanel.SetActive(isIdTaskStep
                && (state == ExperimentState.TaskRunning
                 || state == ExperimentState.TaskComplete));
        }
    }

    private IEnumerator AutoHideAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_zoneBPanel != null) _zoneBPanel.SetActive(false);
        _autoHideCoroutine = null;
    }

    private void RefreshIdQRList()
    {
        if (_idQRListText == null) return;
        var ids = new System.Collections.Generic.List<string>(_detectedTaskQRIds);
        ids.Sort();
        if (ids.Count == 0) { _idQRListText.text = "QR: 検出中..."; return; }
        var sb = new System.Text.StringBuilder("QR: ");
        foreach (var id in ids)
            sb.Append(id == _idCurrentTarget ? $" [►{id}]" : $"  {id} ");
        _idQRListText.text = sb.ToString();
    }

    private static bool IsTaskQRId(string id) =>
        !string.IsNullOrEmpty(id) && id.Length == 1 && char.IsLetter(id[0]);

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

        if (_instructionText != null)
        {
            _instructionText.text = $"[キャリブ確認] Workerメッシュ表示: {(_meshVisible ? "ON ▶ 映像で位置確認してください" : "OFF")}   (M キーで切替)";
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
