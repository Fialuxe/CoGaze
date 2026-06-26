using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;   // EventSystem, BaseInputModule, OVRInputModule (Meta XR Core declares OVRInputModule in this namespace)
using Photon.Pun;

/// <summary>
/// Manages NASA-TLX (post-condition) and SSQ (post-experiment) questionnaires
/// for the Worker (Quest 3).
///
/// WIRING REQUIREMENTS:
/// - Attach this script to a GameObject that also has a PhotonView.
///   Either place it on a scene object with a fixed ViewID, or use
///   PhotonNetwork.Instantiate so both clients own the same view.
/// - VR pointer input is set up automatically at runtime on the Worker:
///   an OVRRaycaster is added to the canvas, and an EventSystem carrying an
///   OVRInputModule (rayTransform = active controller anchor, click = trigger)
///   is created/reused so the buttons receive controller-laser pointer events on
///   Quest. Direct-touch (poke) input is also set up. No scene wiring is required.
///
/// Usage (called by SceneBootstrapper2):
///   questionnaireManager.participantNumber = participantNumber;
///   questionnaireManager.ShowNASATLX(conditionIndex, conditionName);
///   questionnaireManager.ShowSSQ();
///   // subscribe:
///   questionnaireManager.OnQuestionnaireComplete += () => { ... };
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class QuestionnaireManager : MonoBehaviourPun
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("UI References (leave null to auto-build at runtime)")]
    [SerializeField] private Canvas questionnaireCanvas;

    [Header("Optional — assign a font with Japanese/CJK glyphs (e.g. NotoSansCJK)")]
    public Font japaneseFont;

    [Header("WorldSpace Canvas position (metres ahead of camera)")]
    public float   panelDistance = 1.0f;
    public Vector2 panelSizeMm   = new Vector2(520f, 400f);
    public float   panelScaleM   = 0.001f;

    [Header("Participant")]
    public int    participantNumber = 0;
    public string participantId     = "";

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Fired on ALL clients when the Worker submits any questionnaire round.</summary>
    public event Action OnQuestionnaireComplete;

    // ─── NASA-TLX label strings ───────────────────────────────────────────────

    private static readonly string[] NasaLabels =
    {
        "Mental Demand\n(精神的要求)\n0 = 低  /  6 = 高",
        "Physical Demand\n(身体的要求)\n0 = 低  /  6 = 高",
        "Temporal Demand\n(時間的要求)\n0 = 低  /  6 = 高",
        "Performance\n(作業成績)\n0 = 完璧  /  6 = 失敗",
        "Effort\n(努力)\n0 = 低  /  6 = 高",
        "Frustration\n(フラストレーション)\n0 = 低  /  6 = 高"
    };

    // ─── SSQ label strings ────────────────────────────────────────────────────

    private static readonly string[] SsqLabels =
    {
        "General discomfort (全般的不快感)",
        "Fatigue (疲労)",
        "Headache (頭痛)",
        "Eye strain (目の疲れ)",
        "Difficulty focusing (焦点が合わない)",
        "Salivation increasing (唾液増加)",
        "Sweating (発汗)",
        "Nausea (吐き気)",
        "Difficulty concentrating (集中困難)",
        "Fullness of head (頭が重い)",
        "Blurred vision (視野のぼやけ)",
        "Dizziness with eyes open (目を開けたときのめまい)",
        "Dizziness with eyes closed (目を閉じたときのめまい)",
        "Vertigo (めまい・回転感覚)",
        "Stomach awareness (胃の違和感)",
        "Burping (げっぷ)"
    };

    // ─── JSON data model (JsonUtility-compatible — all fields snake_case) ─────

    [Serializable]
    private class NasaScores
    {
        public int mental;
        public int physical;
        public int temporal;
        public int performance;
        public int effort;
        public int frustration;
    }

    [Serializable]
    private class NasaTlxEntry
    {
        public int        condition_index;
        public string     condition_name;
        public NasaScores scores;
    }

    [Serializable]
    private class SsqData
    {
        public int[] scores;   // 16 values, 0-3 each
        public int   total;    // raw sum (max 48)
    }

    [Serializable]
    private class QuestionnaireRoot
    {
        public string             participant_id;
        public string             timestamp;
        public List<NasaTlxEntry> nasa_tlx = new List<NasaTlxEntry>();
        public SsqData            ssq;
    }

    // ─── Runtime state ────────────────────────────────────────────────────────

    private QuestionnaireRoot _data;
    private string            _saveFilePath;    // computed lazily on first SaveJson
    private bool              _isVisible    = false;
    private Transform         _camTransform = null;

    private enum Mode { None, NasaTLX, SSQ }
    private Mode   _currentMode    = Mode.None;
    private int    _conditionIndex;
    private string _conditionName;

    private int   _itemIndex;     // current question index
    private int   _selectedScore; // -1 = nothing selected yet
    private int[] _answers;       // current round raw answers

    // Runtime UI handles
    private GameObject _canvasGo;
    private Text       _titleText;
    private Text       _itemText;
    private Text       _progressText;
    private GameObject _buttonRow;
    private Button[]   _scoreButtons;
    private Button     _nextButton;
    private Text       _nextButtonLabel;

    // VR pointer (Worker only). Created/reused at runtime so pointer events
    // reach the UI buttons. We never destroy a shared EventSystem; only
    // the poke input is torn down on hide.
    private OVRInputModule         _ovrInputModule;
    private BaseInputModule[]      _suspendedModules;   // sibling modules we disabled to avoid conflicts
    private QuestionnairePokeInput  _poke;       // direct-touch ("touch panel") input
    private GameObject             _pokeGo;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        _data = new QuestionnaireRoot();

        // Try to auto-load a Japanese font from Resources/Fonts/ if none assigned.
        // Place e.g. NotoSansJP-Regular.ttf in Assets/Resources/Fonts/ to activate.
        if (japaneseFont == null)
        {
            japaneseFont = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
            if (japaneseFont == null) japaneseFont = Resources.Load<Font>("Fonts/NotoSansCJK-Regular");
            if (japaneseFont == null) japaneseFont = Resources.Load<Font>("Fonts/NotoSansJP");
            if (japaneseFont != null)
                Debug.Log($"[QuestionnaireManager] Japanese font auto-loaded: {japaneseFont.name}");
        }
    }

    // ─── Public show / hide ───────────────────────────────────────────────────

    /// <summary>
    /// Show NASA-TLX (6 items, 0-6 scale) after a condition ends.
    /// No-op on Expert client.
    /// </summary>
    public void ShowNASATLX(int conditionIndex, string conditionName)
    {
        if (RoleManager.LocalRole != RoleManager.ROLE_WORKER) return;

        _currentMode    = Mode.NasaTLX;
        _conditionIndex = conditionIndex;
        _conditionName  = conditionName;
        _answers        = new int[NasaLabels.Length];

        EnsureCanvas();
        SetupVRPointer();                                // controller-laser clicks
        BuildItemLayout(maxScore: 6, buttonCount: 7);   // scores 0-6
        ShowItem(0);
        SetVisible(true);
    }

    /// <summary>
    /// Show SSQ (16 items, 0-3 scale) after all conditions end.
    /// No-op on Expert client.
    /// </summary>
    public void ShowSSQ()
    {
        if (RoleManager.LocalRole != RoleManager.ROLE_WORKER) return;

        _currentMode = Mode.SSQ;
        _answers     = new int[SsqLabels.Length];

        EnsureCanvas();
        SetupVRPointer();                                // controller-laser clicks
        BuildItemLayout(maxScore: 3, buttonCount: 4);   // scores 0-3
        ShowItem(0);
        SetVisible(true);
    }

    /// <summary>Hide the questionnaire panel (e.g. experiment aborted).</summary>
    public void Hide()
    {
        SetVisible(false);
        _currentMode = Mode.None;
    }

    private void Update()
    {
        // Smoothly follow the camera while the questionnaire panel is visible.
        if (!_isVisible || _canvasGo == null || _camTransform == null) return;

        // Freeze the follow while the user is reaching in to touch, so the panel does not
        // drift out from under their fingertip.
        if (_poke != null && _poke.IsEngaged) return;

        Vector3 fwd = Vector3.ProjectOnPlane(_camTransform.forward, Vector3.up);
        if (fwd == Vector3.zero) fwd = _camTransform.forward;
        fwd = fwd.normalized;

        Vector3    targetPos = _camTransform.position + fwd * panelDistance;
        Quaternion targetRot = Quaternion.LookRotation(fwd, Vector3.up);

        _canvasGo.transform.position = Vector3.Lerp(_canvasGo.transform.position, targetPos, Time.deltaTime * 3f);
        _canvasGo.transform.rotation = Quaternion.Slerp(_canvasGo.transform.rotation, targetRot, Time.deltaTime * 3f);
    }

    private void OnDestroy()
    {
        // Clear all subscribers to prevent stale delegate invocations
        OnQuestionnaireComplete = null;

        // Restore any input modules we suspended, then drop our poke input. We do NOT
        // destroy the EventSystem / OVRInputModule — they may be shared and the
        // module installs a static singleton.
        TeardownVRPointer();
        if (_pokeGo != null) Destroy(_pokeGo);

        if (_buttonRow != null) Destroy(_buttonRow);
        if (_canvasGo != null) Destroy(_canvasGo);
    }

    // ─── Canvas construction ──────────────────────────────────────────────────

    private void EnsureCanvas()
    {
        // If an Inspector-assigned canvas is set, use its GameObject as the root.
        // NOTE: In this case _titleText/_itemText/_progressText remain null unless
        //       the integrator also wires those references; the auto-build path is
        //       the recommended approach.
        if (questionnaireCanvas != null && _canvasGo == null)
        {
            _canvasGo = questionnaireCanvas.gameObject;
            return;
        }

        if (_canvasGo != null) return;   // already built

        _canvasGo = new GameObject("QuestionnaireManager_Canvas");

        var canvas = _canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        // OVRRaycaster (a GraphicRaycaster subclass) is required so OVRInputModule's
        // world-space ray can hit these UI buttons. It replaces the plain
        // GraphicRaycaster — do NOT add both.
        _canvasGo.AddComponent<OVRRaycaster>();

        // OVRRaycaster.eventCamera returns canvas.worldCamera and uses it for
        // WorldToScreenPoint, so assign it deterministically (don't rely on
        // OVRRaycaster.Start auto-filling it). Use the center-eye camera if present.
        var camRig = FindAnyObjectByType<OVRCameraRig>();
        if (camRig != null && camRig.centerEyeAnchor != null)
        {
            var eyeCam = camRig.centerEyeAnchor.GetComponent<Camera>();
            if (eyeCam != null) canvas.worldCamera = eyeCam;
        }
        if (canvas.worldCamera == null && Camera.main != null)
            canvas.worldCamera = Camera.main;

        var rt = _canvasGo.GetComponent<RectTransform>();
        rt.sizeDelta = panelSizeMm;
        _canvasGo.transform.localScale = Vector3.one * panelScaleM;

        // ── Background ──
        var bg    = MakeChild("BG", _canvasGo.transform);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.05f, 0.07f, 0.22f, 0.92f);
        StretchFill(bg.GetComponent<RectTransform>());

        // ── Left accent bar ──
        var accent    = MakeChild("Accent", _canvasGo.transform);
        var accentImg = accent.AddComponent<Image>();
        accentImg.color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var accentRt = accent.GetComponent<RectTransform>();
        accentRt.anchorMin = Vector2.zero;
        accentRt.anchorMax = new Vector2(0f, 1f);
        accentRt.offsetMin = Vector2.zero;
        accentRt.offsetMax = new Vector2(4f, 0f);

        // ── Title (top ~10 %) ──
        _titleText = MakeText("Title", _canvasGo.transform,
            new Vector2(0.03f, 0.89f), new Vector2(0.97f, 0.99f),
            "アンケート", 22, TextAnchor.MiddleCenter, new Color(0.6f, 0.9f, 1f));

        MakeDivider(_canvasGo.transform, 0.88f);

        // ── Progress (right-aligned, 82-88 %) ──
        _progressText = MakeText("Progress", _canvasGo.transform,
            new Vector2(0.03f, 0.82f), new Vector2(0.97f, 0.89f),
            "", 15, TextAnchor.MiddleRight, Color.gray);

        // ── Question text (50-83 %) ──
        _itemText = MakeText("ItemText", _canvasGo.transform,
            new Vector2(0.04f, 0.50f), new Vector2(0.96f, 0.83f),
            "", 20, TextAnchor.MiddleCenter, Color.white);

        questionnaireCanvas = canvas;
    }

    // ─── VR pointer (Worker only) ─────────────────────────────────────────────

    /// <summary>
    /// Ensure the questionnaire canvas can receive controller-laser pointer clicks:
    ///  1. Make sure the canvas has an OVRRaycaster (so OVRInputModule can hit it).
    ///  2. Reuse or create exactly ONE EventSystem carrying an OVRInputModule, with
    ///     rayTransform pointed at the active controller anchor and the click bound
    ///     to the index trigger.
    /// Idempotent and self-contained — safe to call every time the panel is shown.
    /// </summary>
    private void SetupVRPointer()
    {
        if (_canvasGo == null) return;

        // 1) Guarantee an OVRRaycaster on the active canvas. Covers the
        //    Inspector-assigned-canvas path (where EnsureCanvas returns early and
        //    never adds one). OVRRaycaster requires a Canvas, which is present here.
        if (_canvasGo.GetComponent<OVRRaycaster>() == null)
        {
            // Remove a plain GraphicRaycaster if one snuck in, so we don't run two.
            var plain = _canvasGo.GetComponent<GraphicRaycaster>();
            if (plain != null && !(plain is OVRRaycaster)) Destroy(plain);
            _canvasGo.AddComponent<OVRRaycaster>();
        }

        // Ensure the canvas has an event camera for screen-space conversion.
        var canvas = _canvasGo.GetComponent<Canvas>();
        var camRig = FindAnyObjectByType<OVRCameraRig>();
        if (canvas != null && canvas.worldCamera == null)
        {
            if (camRig != null && camRig.centerEyeAnchor != null)
            {
                var eyeCam = camRig.centerEyeAnchor.GetComponent<Camera>();
                if (eyeCam != null) canvas.worldCamera = eyeCam;
            }
            if (canvas.worldCamera == null && Camera.main != null)
                canvas.worldCamera = Camera.main;
        }

        // 2) Reuse an existing EventSystem if present; otherwise create one.
        //    NEVER create a duplicate EventSystem.
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null) eventSystem = FindAnyObjectByType<EventSystem>();

        if (eventSystem == null)
        {
            var esGo = new GameObject("QuestionnaireEventSystem");
            eventSystem = esGo.AddComponent<EventSystem>();
        }

        // Add (or reuse) an OVRInputModule on the EventSystem. Disable any other
        // BaseInputModule siblings (e.g. the scene's InputSystemUIInputModule or a
        // StandaloneInputModule) so they don't fight over input. We remember them
        // so they can be restored when the panel hides.
        _ovrInputModule = eventSystem.GetComponent<OVRInputModule>();
        if (_ovrInputModule == null)
            _ovrInputModule = eventSystem.gameObject.AddComponent<OVRInputModule>();

        var siblings = eventSystem.GetComponents<BaseInputModule>();
        var suspended = new List<BaseInputModule>();
        foreach (var m in siblings)
        {
            if (m == null || m == _ovrInputModule) continue;
            if (m.enabled)
            {
                m.enabled = false;
                suspended.Add(m);
            }
        }
        _suspendedModules = suspended.ToArray();

        _ovrInputModule.enabled = true;
        // Required so the module activates on Android/Quest (no mouse present):
        _ovrInputModule.allowActivationOnMobileDevice = true;
        // Click with the controller index trigger (better laser UX than A/X).
        _ovrInputModule.joyPadClickButton = OVRInput.Button.PrimaryIndexTrigger;

        // Point the ray at the best available anchor right away so the very first
        // frame has a valid rayTransform. The laser keeps this updated thereafter.
        if (camRig != null)
        {
            Transform anchor = null;
            if (camRig.rightControllerAnchor != null) anchor = camRig.rightControllerAnchor;
            else if (camRig.leftControllerAnchor != null) anchor = camRig.leftControllerAnchor;
            else if (camRig.centerEyeAnchor != null) anchor = camRig.centerEyeAnchor;
            if (anchor != null) _ovrInputModule.rayTransform = anchor;
        }

        // 3) Visible laser line — DISABLED. The laser was confusing and did not operate
        // reliably; the questionnaire is now touch-only (QuestionnaireLaserInput removed).

        // 4) Direct-touch ("touch panel") input — poke buttons with a fingertip or controller tip.
        if (_pokeGo == null)
        {
            _pokeGo = new GameObject("QuestionnairePoke");
            _poke   = _pokeGo.AddComponent<QuestionnairePokeInput>();
        }
        if (_poke != null) _poke.Configure(_canvasGo.GetComponent<RectTransform>(), camRig);
        _pokeGo.SetActive(true);
    }

    /// <summary>
    /// Deactivate poke input and re-enable any input modules we suspended.
    /// The EventSystem / OVRInputModule may be shared, so it is left in
    /// place (OVRInputModule.Awake also installs a static singleton).
    /// </summary>
    private void TeardownVRPointer()
    {
        if (_pokeGo != null) _pokeGo.SetActive(false);

        // Disable our OVR module BEFORE re-enabling the scene's own module(s), so
        // the EventSystem returns to exactly its prior state (one active module).
        // This avoids input contention during the task between questionnaire rounds.
        if (_ovrInputModule != null) _ovrInputModule.enabled = false;

        if (_suspendedModules != null)
        {
            foreach (var m in _suspendedModules)
                if (m != null) m.enabled = true;
            _suspendedModules = null;
        }
    }

    /// <summary>
    /// Rebuild the score button row + Next/Submit button for the current questionnaire type.
    /// Destroys the previous row so it is safe to call multiple times.
    /// </summary>
    private void BuildItemLayout(int maxScore, int buttonCount)
    {
        if (_buttonRow != null) { Destroy(_buttonRow); _buttonRow = null; }
        _scoreButtons    = null;
        _nextButton      = null;
        _nextButtonLabel = null;

        // ── Score button row (18-45 %) ──
        _buttonRow = MakeChild("ButtonRow", _canvasGo.transform);
        var rowRt = _buttonRow.GetComponent<RectTransform>();
        rowRt.anchorMin = new Vector2(0.04f, 0.25f);
        rowRt.anchorMax = new Vector2(0.96f, 0.46f);
        rowRt.offsetMin = rowRt.offsetMax = Vector2.zero;

        _scoreButtons = new Button[buttonCount];

        for (int i = 0; i < buttonCount; i++)
        {
            int score = i;   // captured in lambda

            var btnGo = MakeChild($"ScoreBtn_{score}", _buttonRow.transform);
            var btnRt = btnGo.GetComponent<RectTransform>();
            float xMin = (float)i / buttonCount;
            float xMax = (float)(i + 1) / buttonCount;
            btnRt.anchorMin = new Vector2(xMin + 0.006f, 0f);
            btnRt.anchorMax = new Vector2(xMax - 0.006f, 1f);
            btnRt.offsetMin = btnRt.offsetMax = Vector2.zero;

            var btnImg = btnGo.AddComponent<Image>();
            btnImg.color = new Color(0.20f, 0.30f, 0.55f, 0.95f);

            var btn = btnGo.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor      = new Color(0.20f, 0.30f, 0.55f, 0.95f);
            colors.highlightedColor = new Color(0.35f, 0.55f, 0.80f, 1.00f);
            colors.pressedColor     = new Color(0.10f, 0.70f, 0.40f, 1.00f);
            colors.selectedColor    = new Color(0.10f, 0.80f, 0.45f, 1.00f);
            btn.colors        = colors;
            btn.targetGraphic = btnImg;

            btn.onClick.AddListener(() => OnScoreButtonClicked(score));

            MakeText($"ScoreLabel_{score}", btnGo.transform,
                Vector2.zero, Vector2.one,
                score.ToString(), 28, TextAnchor.MiddleCenter, Color.white);

            _scoreButtons[i] = btn;
        }

        // ── Scale hint below buttons (17-25 %) ──
        string hint = maxScore == 6
            ? "← 低 / 良い   0 — 1 — 2 — 3 — 4 — 5 — 6   高 / 悪い →"
            : "← なし   0 — 1 — 2 — 3   ひどく →";

        MakeText("ScaleHint", _canvasGo.transform,
            new Vector2(0.04f, 0.17f), new Vector2(0.96f, 0.25f),
            hint, 13, TextAnchor.MiddleCenter, new Color(0.65f, 0.65f, 0.65f));

        MakeDivider(_canvasGo.transform, 0.16f);

        // ── Next / Submit button (3-14 %) — starts disabled ──
        var nextGo  = MakeChild("NextBtn", _canvasGo.transform);
        var nextRt  = nextGo.GetComponent<RectTransform>();
        nextRt.anchorMin = new Vector2(0.60f, 0.03f);
        nextRt.anchorMax = new Vector2(0.96f, 0.14f);
        nextRt.offsetMin = nextRt.offsetMax = Vector2.zero;

        var nextImg = nextGo.AddComponent<Image>();
        nextImg.color = new Color(0.15f, 0.50f, 0.25f, 0.95f);

        _nextButton = nextGo.AddComponent<Button>();
        var nc = _nextButton.colors;
        nc.normalColor      = new Color(0.15f, 0.50f, 0.25f, 0.95f);
        nc.highlightedColor = new Color(0.20f, 0.70f, 0.35f, 1.00f);
        nc.pressedColor     = new Color(0.08f, 0.35f, 0.15f, 1.00f);
        nc.disabledColor    = new Color(0.25f, 0.25f, 0.25f, 0.60f);
        _nextButton.colors        = nc;
        _nextButton.targetGraphic = nextImg;
        _nextButton.interactable  = false;   // enabled after a score is selected
        _nextButton.onClick.AddListener(OnNextButtonClicked);

        _nextButtonLabel = MakeText("NextLabel", nextGo.transform,
            Vector2.zero, Vector2.one,
            "Next →", 20, TextAnchor.MiddleCenter, Color.white);
    }

    // ─── Navigation ──────────────────────────────────────────────────────────

    private void ShowItem(int index)
    {
        _itemIndex     = index;
        _selectedScore = -1;

        string[] labels = _currentMode == Mode.NasaTLX ? NasaLabels : SsqLabels;
        int      total  = labels.Length;
        bool     isLast = (index == total - 1);

        // Title — deliberately hides condition name/index to preserve single-blind design.
        if (_titleText != null)
            _titleText.text = _currentMode == Mode.NasaTLX
                ? $"NASA-TLX — 第 {_data.nasa_tlx.Count + 1} 回"
                : "SSQ — Simulator Sickness Questionnaire";

        // Progress
        if (_progressText != null)
            _progressText.text = $"{index + 1} / {total}";

        // Question text
        if (_itemText != null)
            _itemText.text = labels[index];

        // Reset score buttons
        HighlightScore(-1);

        // Update Next/Submit button label and disable until a score is picked
        if (_nextButton != null)
        {
            _nextButton.interactable = false;
            if (_nextButtonLabel != null)
                _nextButtonLabel.text = isLast ? "Submit ✓" : "Next →";
        }
    }

    private void OnScoreButtonClicked(int score)
    {
        _answers[_itemIndex] = score;
        _selectedScore       = score;
        HighlightScore(score);

        // Unlock the Next/Submit button now that a selection has been made
        if (_nextButton != null)
            _nextButton.interactable = true;
    }

    private void OnNextButtonClicked()
    {
        if (_selectedScore < 0) return;   // guard: no selection yet

        string[] labels = _currentMode == Mode.NasaTLX ? NasaLabels : SsqLabels;
        int nextIndex = _itemIndex + 1;

        if (nextIndex < labels.Length)
        {
            ShowItem(nextIndex);
        }
        else
        {
            SubmitCurrentRound();
        }
    }

    private void HighlightScore(int score)
    {
        if (_scoreButtons == null) return;
        for (int i = 0; i < _scoreButtons.Length; i++)
        {
            if (_scoreButtons[i] == null) continue;
            var img = _scoreButtons[i].GetComponent<Image>();
            if (img == null) continue;
            img.color = (i == score)
                ? new Color(0.10f, 0.75f, 0.42f, 1.00f)   // selected: green
                : new Color(0.20f, 0.30f, 0.55f, 0.95f);  // unselected: blue-grey
        }
    }

    // ─── Submission ───────────────────────────────────────────────────────────

    private void SubmitCurrentRound()
    {
        if (_currentMode == Mode.NasaTLX)
        {
            _data.nasa_tlx.Add(new NasaTlxEntry
            {
                condition_index = _conditionIndex,
                condition_name  = _conditionName,
                scores = new NasaScores
                {
                    mental      = _answers[0],
                    physical    = _answers[1],
                    temporal    = _answers[2],
                    performance = _answers[3],
                    effort      = _answers[4],
                    frustration = _answers[5]
                }
            });

            SaveJson();   // persist after every round — crash-safe

            SetVisible(false);
            _currentMode = Mode.None;
            photonView.RPC(nameof(RPC_QuestionnaireComplete), RpcTarget.All);
        }
        else  // SSQ
        {
            int total = 0;
            foreach (int v in _answers) total += v;

            _data.ssq = new SsqData
            {
                scores = (int[])_answers.Clone(),
                total  = total
            };

            SaveJson();
            Debug.Log($"[QuestionnaireManager] SSQ submitted. Total={total}. Path={_saveFilePath}");

            SetVisible(false);
            _currentMode = Mode.None;
            photonView.RPC(nameof(RPC_QuestionnaireComplete), RpcTarget.All);
        }
    }

    private void EnsureSavePath()
    {
        if (_saveFilePath != null) return;

        // Computed lazily so participantId/participantNumber is set by the bootstrapper before use.
        string pid = !string.IsNullOrEmpty(participantId) ? participantId : $"P{participantNumber:D2}";
        string ts  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _saveFilePath = Path.Combine(
            Application.persistentDataPath,
            $"questionnaire_{pid}_{ts}.json");

        // Set the session timestamp now too.
        _data.timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    }

    private void SaveJson()
    {
        EnsureSavePath();

        // Re-sync participant_id in case it was set after Awake.
        _data.participant_id = !string.IsNullOrEmpty(participantId) ? participantId : $"P{participantNumber:D2}";

        string json = JsonUtility.ToJson(_data, prettyPrint: true);

        // Local save (HMD backup — crash-safe atomic write).
        try
        {
            string tmpPath = _saveFilePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(_saveFilePath)) File.Delete(_saveFilePath);
            File.Move(tmpPath, _saveFilePath);
            Debug.Log($"[QuestionnaireManager] JSON saved → {_saveFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[QuestionnaireManager] Local save failed: {ex.Message}");
        }

        // Forward to Expert (PC) so the researcher can access the data without ADB.
        if (PhotonNetwork.InRoom && photonView != null)
            photonView.RPC(nameof(RPC_ForwardQuestionnaireJson), RpcTarget.Others, json, _data.participant_id);
    }

    // ─── Photon RPC ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called on ALL clients (RpcTarget.All) when the Worker submits a round.
    /// ExperimentManager2 subscribes to OnQuestionnaireComplete to advance.
    /// </summary>
    [PunRPC]
    private void RPC_QuestionnaireComplete()
    {
        Debug.Log("[QuestionnaireManager] RPC_QuestionnaireComplete received.");
        OnQuestionnaireComplete?.Invoke();
    }

    /// <summary>
    /// Received by the Expert (PC) each time the Worker saves a questionnaire snapshot.
    /// Writes the JSON to the PC's data directory so researchers can access it without ADB.
    /// The file is overwritten on each submit, so only the final cumulative JSON persists.
    /// </summary>
    [PunRPC]
    private void RPC_ForwardQuestionnaireJson(string json, string pid)
    {
        if (RoleManager.LocalRole != RoleManager.ROLE_EXPERT) return;

        // Mirror the same base-directory logic used by FileLogger on PC.
        string dir = Application.platform == RuntimePlatform.Android
            ? Application.persistentDataPath
            : Path.Combine(Application.dataPath, "..");
        string path = Path.Combine(dir, $"questionnaire_{pid}.json");
        try
        {
            File.WriteAllText(path, json);
            Debug.Log($"[QuestionnaireManager] PC copy saved → {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[QuestionnaireManager] PC save failed: {ex.Message}");
        }
    }

    // ─── WorldSpace canvas positioning ───────────────────────────────────────

    private void SetVisible(bool visible)
    {
        if (_canvasGo == null) return;

        if (visible)
        {
            // Place panel ahead of camera. On Quest3, Camera.main may be null after
            // Worker setup disables the main cam; fall back to OVRCameraRig's center eye.
            Camera cam = Camera.main;
            Transform camTransform = cam != null ? cam.transform : null;
            if (camTransform == null)
            {
                var rig = FindAnyObjectByType<OVRCameraRig>();
                if (rig != null) camTransform = rig.centerEyeAnchor;
            }

            if (camTransform != null)
            {
                Vector3 forward = Vector3.ProjectOnPlane(camTransform.forward, Vector3.up);
                if (forward == Vector3.zero) forward = camTransform.forward;
                forward = forward.normalized;

                _canvasGo.transform.position = camTransform.position + forward * panelDistance;
                _canvasGo.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                _camTransform = camTransform;
            }
        }

        // Deactivate poke input (and restore suspended input modules) when hiding.
        if (!visible) TeardownVRPointer();

        _isVisible = visible;
        _canvasGo.SetActive(visible);
    }

    // ─── UI helpers ──────────────────────────────────────────────────────────

    private static GameObject MakeChild(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    /// <summary>Creates a legacy Text component. Prefers <see cref="japaneseFont"/> for CJK support.</summary>
    private Text MakeText(string name, Transform parent,
                          Vector2 anchorMin, Vector2 anchorMax,
                          string defaultText, int fontSize,
                          TextAnchor alignment, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();

        t.text               = defaultText;
        t.fontSize           = fontSize;
        t.alignment          = alignment;
        t.color              = color;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow   = VerticalWrapMode.Overflow;

        // Prefer the Japanese font assigned in the Inspector (required for CJK glyphs).
        // Falls back to Unity's built-in fonts (no CJK — labels will show tofu without it).
        Font f = japaneseFont;
        if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (f != null) t.font = f;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        return t;
    }

    private void MakeDivider(Transform parent, float yAnchor)
    {
        var go  = MakeChild("Divider", parent);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.3f, 0.75f, 1f, 0.25f);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.03f, yAnchor);
        rt.anchorMax = new Vector2(0.97f, yAnchor);
        rt.offsetMin = new Vector2(0f, -0.5f);
        rt.offsetMax = new Vector2(0f,  0.5f);
    }

    private static void StretchFill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
