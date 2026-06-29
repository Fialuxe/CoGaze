using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;   // EventSystem, BaseInputModule, OVRInputModule (Meta XR Core declares OVRInputModule in this namespace)
using Photon.Pun;

[RequireComponent(typeof(PhotonView))]
public class QuestionnaireManager : MonoBehaviourPun
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("UI References (leave null to auto-build at runtime)")]
    [SerializeField] private Canvas questionnaireCanvas;

    [Header("Optional — assign a font with Japanese/CJK glyphs (e.g. NotoSansCJK)")]
    public Font japaneseFont;

    [Header("WorldSpace Canvas position (metres ahead of camera)")]
    public float   panelDistance = 1.5f;
    public Vector2 panelSizeMm   = new Vector2(520f, 400f);
    public float   panelScaleM   = 0.001f;

    [Header("Participant")]
    public int    participantNumber = 0;
    public string participantId     = "";

    // ─── Public API ───────────────────────────────────────────────────────────

    public event Action              OnQuestionnaireComplete;
    // Raised via RpcTarget.All only (not locally) — ExperimentManager2 gates Finished screen on this.
    public event System.Action       OnSurveySubmitted;

    // ─── NASA-TLX dimensions (SINGLE SOURCE OF TRUTH — CQ 2-6) ────────────────
    //
    // Each dimension pairs the label shown to the subject with the exact NasaScores
    // field it writes into. Because the label and the output-field assignment live in
    // ONE place, they cannot drift out of alignment by editing two separate lists.
    // Add/remove a dimension here and the Assign delegate forces a matching NasaScores
    // field at compile time. The runtime length assert in SubmitCurrentRound guards the
    // remaining invariant: that the dimension count still matches NasaScores' field count.
    private sealed class NasaDimension
    {
        public readonly string                   Label;
        public readonly Action<NasaScores, int>  Assign;
        public NasaDimension(string label, Action<NasaScores, int> assign)
        {
            Label  = label;
            Assign = assign;
        }
    }

    // Number of int fields on NasaScores. The assert in SubmitCurrentRound fails LOUDLY
    // if s_nasaDimensions ever stops matching this, rather than writing misaligned data.
    private const int k_nasaScoresFieldCount = 6;

    private static readonly NasaDimension[] s_nasaDimensions =
    {
        new NasaDimension(
            "Mental Demand（精神的要求）\n" +
            "思考・判断・計算・記憶・注視・探索など\n" +
            "どれくらい頭を使う作業でしたか？\n" +
            "0 = 非常に低い  /  6 = 非常に高い",
            (s, v) => s.mental = v),

        new NasaDimension(
            "Physical Demand（身体的要求）\n" +
            "押す・引く・回す・コントローラ操作など\n" +
            "どれくらい身体を使う作業でしたか？\n" +
            "0 = 非常に低い  /  6 = 非常に高い",
            (s, v) => s.physical = v),

        new NasaDimension(
            "Temporal Demand（時間的要求）\n" +
            "作業のペースやスピードに対して\n" +
            "どれくらい時間的なプレッシャーを感じましたか？\n" +
            "0 = ゆったり  /  6 = 非常に急いでいた",
            (s, v) => s.temporal = v),

        new NasaDimension(
            "Performance（作業成績）\n" +
            "設定された（または自分で設定した）目標を\n" +
            "どれくらい達成できたと思いますか？\n" +
            "0 = 完璧  /  6 = 全く達成できなかった",
            (s, v) => s.performance = v),

        new NasaDimension(
            "Effort（努力）\n" +
            "このレベルの作業成績を達成するために\n" +
            "どれくらい頑張る必要がありましたか？\n" +
            "0 = 非常に低い  /  6 = 非常に高い",
            (s, v) => s.effort = v),

        new NasaDimension(
            "Frustration（フラストレーション）\n" +
            "不安・苛立ち・ストレス・悩みをどれくらい感じましたか？\n" +
            "（対: 安心感・満足感・リラックス）\n" +
            "0 = 非常に低い  /  6 = 非常に高い",
            (s, v) => s.frustration = v),
    };

    // Display labels, DERIVED from the single source above so the UI can index them
    // cheaply while remaining impossible to reorder independently of the field mapping.
    private static readonly string[] s_nasaLabels = Builds_nasaLabels();

    private static string[] Builds_nasaLabels()
    {
        var labels = new string[s_nasaDimensions.Length];
        for (int i = 0; i < s_nasaDimensions.Length; i++)
            labels[i] = s_nasaDimensions[i].Label;
        return labels;
    }

    // ─── SSQ label strings ────────────────────────────────────────────────────

    private static readonly string[] s_ssqLabels =
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
        public string     task_type;       // "identification" / "assembly" — two NASA blocks per condition
        public NasaScores scores;
    }

    // One gaze-construct rating (comprehension / usefulness / accuracy manipulation-check).
    // score = -1 with missing=true marks a STRUCTURAL absence (e.g. usefulness/accuracy in the
    // NoGaze control): recorded explicitly so the dataset stays rectangular, never imputed.
    [Serializable]
    private class GazeItemEntry
    {
        public int    condition_index;
        public string condition_name;
        public string task_type;   // "identification" / "assembly" / "condition"
        public string construct;   // "comprehension" / "usefulness" / "accuracy_mc"
        public int    score;       // 0-6, or -1 when missing
        public bool   missing;     // true = structural NoGaze absence (not answered)
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
        public string              participant_id;
        public string              timestamp;
        public List<NasaTlxEntry>  nasa_tlx   = new List<NasaTlxEntry>();
        public List<GazeItemEntry> gaze_items = new List<GazeItemEntry>();
        public SsqData             ssq;
    }

    // ─── Runtime state ────────────────────────────────────────────────────────

    private QuestionnaireRoot _data;
    private string            _saveFilePath;    // computed lazily on first SaveJson
    private bool              _isVisible;
    private Transform         _camTransform;

    // ─── Condition-panel item model (NASA ×2 tasks + gaze constructs + accuracy MC) ──
    // Every item in a condition panel is a 0-6 / 7-button rating; only the label and the
    // scale hint vary. Driving the hint per item is the fix for the old high=bad hint
    // bleeding onto high=good gaze items (the former fixed ScaleHint string).
    private enum PanelConstruct { Nasa, Comprehension, Usefulness, AccuracyMC }
    private enum PanelTask      { Identification, Assembly, Condition }

    private sealed class PanelItem
    {
        public string         Label;        // question text (with embedded anchors)
        public string         ScaleHint;    // hint under the buttons (direction depends on construct)
        public string         Section;      // title header (condition-blind)
        public PanelConstruct Construct;
        public PanelTask      Task;
        public int            NasaDimIndex; // valid only when Construct == Nasa
    }

    private enum Mode { None, ConditionPanel, SSQ }
    private Mode   _currentMode    = Mode.None;
    private int    _conditionIndex;
    private string _conditionName;
    private bool   _panelIsNoGaze;                       // NoGaze control → no usefulness/MC items
    private List<PanelItem> _panelItems = new List<PanelItem>();

    private int   _itemIndex;     // current question index
    private int   _selectedScore; // -1 = nothing selected yet
    private int[] _answers;       // current round raw answers

    // Runtime UI handles
    private GameObject _canvasGo;
    private Text       _titleText;
    private Text       _itemText;
    private Text       _progressText;
    private Text       _affordanceText;   // one-line "how to answer" hint (UX16)
    private Text       _scaleHintText;    // direction hint under buttons; set per item (high=good vs high=bad)
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

    // Reticle dot: small crosshair shown on the canvas at the controller ray hit point.
    private Transform      _controllerAnchor;
    private RectTransform  _reticleRt;

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

    // Scale-direction hints (set per item — NASA is high=bad, gaze constructs are high=good).
    private const string k_nasaScaleHint =
        "← 低 / 良い   0 — 1 — 2 — 3 — 4 — 5 — 6   高 / 悪い →";
    private const string k_comprehensionScaleHint =
        "← 分からなかった   0 — 1 — 2 — 3 — 4 — 5 — 6   はっきり分かった →";
    private const string k_usefulnessScaleHint =
        "← 役に立たなかった   0 — 1 — 2 — 3 — 4 — 5 — 6   非常に役立った →";
    private const string k_accuracyScaleHint =
        "← 不正確・不安定   0 — 1 — 2 — 3 — 4 — 5 — 6   正確・安定 →";
    private const string k_nasaSection = "作業負荷について (NASA-TLX)";
    private const string k_gazeSection = "課題と視線について";

    public void ShowNASATLX(int conditionIndex, string conditionName)
    {
        if (RoleManager.LocalRole != RoleManager.ROLE_WORKER) return;

        _currentMode    = Mode.ConditionPanel;
        _conditionIndex = conditionIndex;
        _conditionName  = conditionName;
        _panelIsNoGaze  = IsNoGazeCondition(conditionIndex);
        _panelItems     = BuildConditionPanel(_panelIsNoGaze);
        _answers        = new int[_panelItems.Count];

        EnsureCanvas();
        SetupVRPointer();                                // controller-laser clicks
        BuildItemLayout(maxScore: 6, buttonCount: 7);   // scores 0-6 (constant across the panel)
        ShowItem(0);
        SetVisible(true);
    }

    private static bool IsNoGazeCondition(int conditionIndex)
    {
        if (conditionIndex < 0 || conditionIndex >= ExperimentDesign.Conditions.Length) return false;
        return ExperimentDesign.Conditions[conditionIndex].gaze == GazeMode.None;
    }

    // Ordered item list for one condition panel. Usefulness + accuracy MC are omitted from the
    // PRESENTED list in NoGaze; they are written as structural-missing rows at submit instead.
    private static List<PanelItem> BuildConditionPanel(bool isNoGaze)
    {
        var items = new List<PanelItem>();

        AddNasaBlock(items, PanelTask.Identification, "【識別課題】");
        items.Add(MakeComprehension(PanelTask.Identification));
        if (!isNoGaze) items.Add(MakeUsefulness(PanelTask.Identification));

        AddNasaBlock(items, PanelTask.Assembly, "【組立課題】");
        items.Add(MakeComprehension(PanelTask.Assembly));
        if (!isNoGaze) items.Add(MakeUsefulness(PanelTask.Assembly));

        if (!isNoGaze) items.Add(MakeAccuracyMC());

        return items;
    }

    private static void AddNasaBlock(List<PanelItem> items, PanelTask task, string taskPrefix)
    {
        for (int dim = 0; dim < s_nasaLabels.Length; dim++)
            items.Add(new PanelItem
            {
                Label        = $"{taskPrefix}\n{s_nasaLabels[dim]}",
                ScaleHint    = k_nasaScaleHint,
                Section      = k_nasaSection,
                Construct    = PanelConstruct.Nasa,
                Task         = task,
                NasaDimIndex = dim,
            });
    }

    private static PanelItem MakeComprehension(PanelTask task)
    {
        string prefix = task == PanelTask.Identification ? "【識別課題】" : "【組立課題】";
        string body   = task == PanelTask.Identification
            ? "エキスパートが指示していた対象（どのQRコードか）が、はっきりと分かった"
            : "エキスパートが指示していた「次に置くブロックと置き場所」が、はっきりと分かった";
        return new PanelItem
        {
            Label     = $"{prefix}\n{body}\n0 = 分からなかった ・ 3 = どちらともいえない ・ 6 = はっきり分かった",
            ScaleHint = k_comprehensionScaleHint,
            Section   = k_gazeSection,
            Construct = PanelConstruct.Comprehension,
            Task      = task,
        };
    }

    private static PanelItem MakeUsefulness(PanelTask task)
    {
        string prefix = task == PanelTask.Identification ? "【識別課題】" : "【組立課題】";
        string body   = task == PanelTask.Identification
            ? "エキスパートの視線提示は、対象（QRコード）を見つけて答えるのに役立った"
            : "エキスパートの視線提示は、ブロックを正しく組み立てるのに役立った";
        return new PanelItem
        {
            Label     = $"{prefix}\n{body}\n0 = 役に立たなかった ・ 6 = 非常に役立った",
            ScaleHint = k_usefulnessScaleHint,
            Section   = k_gazeSection,
            Construct = PanelConstruct.Usefulness,
            Task      = task,
        };
    }

    private static PanelItem MakeAccuracyMC() => new PanelItem
    {
        Label     = "【この条件全体】\nエキスパートの視線提示は、対象をどの程度「正確・安定して」指していましたか\n0 = まったく不正確・不安定 ・ 6 = 非常に正確・安定",
        ScaleHint = k_accuracyScaleHint,
        Section   = k_gazeSection,
        Construct = PanelConstruct.AccuracyMC,
        Task      = PanelTask.Condition,
    };

    public void ShowSSQ()
    {
        if (RoleManager.LocalRole != RoleManager.ROLE_WORKER) return;

        _currentMode = Mode.SSQ;
        _answers     = new int[s_ssqLabels.Length];

        EnsureCanvas();
        SetupVRPointer();                                // controller-laser clicks
        BuildItemLayout(maxScore: 3, buttonCount: 4);   // scores 0-3
        ShowItem(0);
        SetVisible(true);
    }

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

        // Update reticle position: project controller ray onto the canvas plane.
        UpdateReticle();
    }

    private void UpdateReticle()
    {
        if (_reticleRt == null || _controllerAnchor == null || _canvasGo == null) return;

        Ray   ray   = new Ray(_controllerAnchor.position, _controllerAnchor.forward);
        Plane plane = new Plane(-_canvasGo.transform.forward, _canvasGo.transform.position);

        if (plane.Raycast(ray, out float dist) && dist > 0.05f && dist < 5f)
        {
            Vector3 worldHit  = ray.GetPoint(dist);
            // InverseTransformPoint converts world coords to canvas local-unit coords.
            // Because the canvas has scale = panelScaleM, the result is already in canvas units.
            Vector3 localHit3 = _canvasGo.transform.InverseTransformPoint(worldHit);
            var     canvasRt  = _canvasGo.GetComponent<RectTransform>();
            Vector2 half      = canvasRt.sizeDelta * 0.5f;

            if (Mathf.Abs(localHit3.x) <= half.x && Mathf.Abs(localHit3.y) <= half.y)
            {
                _reticleRt.anchoredPosition = new Vector2(localHit3.x, localHit3.y);
                _reticleRt.gameObject.SetActive(true);
                return;
            }
        }
        _reticleRt.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        // Clear all subscribers to prevent stale delegate invocations
        OnQuestionnaireComplete = null;
        OnSurveySubmitted       = null;

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

        // Re-entrancy guard (CQ 2-7): only capture & suspend the sibling modules if we
        // have NOT already done so. If SetupVRPointer runs twice without an intervening
        // TeardownVRPointer (e.g. ShowNASATLX then ShowSSQ while still visible), the
        // siblings are already disabled, so a fresh scan would record an EMPTY set and
        // strand the scene's real input module disabled forever. Preserving the original
        // captured set lets TeardownVRPointer restore it exactly. TeardownVRPointer nulls
        // _suspendedModules, so a balanced hide→show re-captures correctly.
        if (_suspendedModules == null)
        {
            var siblings  = eventSystem.GetComponents<BaseInputModule>();
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
        }

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

        // 3) Reticle dot: a small white square that follows the controller ray on the canvas
        //    so the subject always knows where they are pointing without a distracting laser.
        if (camRig != null)
        {
            Transform anchor = null;
            if (camRig.rightControllerAnchor != null) anchor = camRig.rightControllerAnchor;
            else if (camRig.leftControllerAnchor  != null) anchor = camRig.leftControllerAnchor;
            _controllerAnchor = anchor;
        }
        if (_reticleRt == null && _canvasGo != null)
        {
            var rGo = new GameObject("PointerReticle");
            rGo.transform.SetParent(_canvasGo.transform, false);
            _reticleRt = rGo.AddComponent<RectTransform>();
            _reticleRt.sizeDelta = new Vector2(18f, 18f);
            var img = rGo.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.85f);
            rGo.SetActive(false);
        }

        // 4) Direct-touch ("touch panel") input — poke buttons with a fingertip or controller tip.
        if (_pokeGo == null)
        {
            _pokeGo = new GameObject("QuestionnairePoke");
            _poke   = _pokeGo.AddComponent<QuestionnairePokeInput>();
        }
        if (_poke != null) _poke.Configure(_canvasGo.GetComponent<RectTransform>(), camRig);
        _pokeGo.SetActive(true);
    }

    private void TeardownVRPointer()
    {
        if (_reticleRt != null) _reticleRt.gameObject.SetActive(false);
        _controllerAnchor = null;

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

        // ── Answer affordance hint (46-50 %), shown only on the first question (UX16) ──
        // The controller laser is invisible (touch-only), so the very first screen must
        // tell the subject HOW to register an answer. ShowItem fills/clears the text.
        _affordanceText = MakeText("AffordanceHint", _canvasGo.transform,
            new Vector2(0.04f, 0.46f), new Vector2(0.96f, 0.50f),
            "", 14, TextAnchor.MiddleCenter, new Color(0.4f, 0.9f, 1f));

        // ── Scale hint below buttons (17-25 %) ──
        // Stored so ShowItem can set it PER ITEM: NASA items are high=bad, gaze items are
        // high=good, and a single fixed hint would mislabel one of them (the old UX landmine).
        string defaultHint = maxScore == 6 ? k_nasaScaleHint : "← なし   0 — 1 — 2 — 3   ひどく →";
        _scaleHintText = MakeText("ScaleHint", _canvasGo.transform,
            new Vector2(0.04f, 0.17f), new Vector2(0.96f, 0.25f),
            defaultHint, 13, TextAnchor.MiddleCenter, new Color(0.65f, 0.65f, 0.65f));

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

        bool      isCondPanel = _currentMode == Mode.ConditionPanel;
        int       total       = isCondPanel ? _panelItems.Count : s_ssqLabels.Length;
        bool      isLast       = (index == total - 1);
        PanelItem item         = isCondPanel ? _panelItems[index] : null;

        // Title — condition name/index deliberately hidden to preserve single-blind design.
        if (_titleText != null)
        {
            _titleText.color = new Color(0.6f, 0.9f, 1f);   // reset (clears any prior save-error red)
            _titleText.text  = isCondPanel
                ? item.Section
                : "SSQ — Simulator Sickness Questionnaire";
        }

        // Progress
        if (_progressText != null)
            _progressText.text = $"{index + 1} / {total}";

        // Per-item scale hint (NASA high=bad vs gaze high=good). SSQ keeps its built-in hint.
        if (_scaleHintText != null && isCondPanel)
            _scaleHintText.text = item.ScaleHint;

        // Answer affordance — only on the first question; cleared thereafter (UX16).
        if (_affordanceText != null)
            _affordanceText.text = (index == 0)
                ? "▼ 下のボタンに指で触れて回答してください (touch a button to answer) ▼"
                : "";

        // Question text
        if (_itemText != null)
            _itemText.text = isCondPanel ? item.Label : s_ssqLabels[index];

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

        int total     = _currentMode == Mode.ConditionPanel ? _panelItems.Count : s_ssqLabels.Length;
        int nextIndex = _itemIndex + 1;

        if (nextIndex < total)
            ShowItem(nextIndex);
        else
            SubmitCurrentRound();
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
        // Idempotency guard (CQ 2-7). A round in progress is always ConditionPanel or SSQ; once
        // submitted it is set to None below. A stray second click (a double-tap before the canvas
        // hides) would otherwise re-run the save and double-write. Bailing here makes Submit safe
        // to invoke twice.
        if (_currentMode != Mode.ConditionPanel && _currentMode != Mode.SSQ) return;

        if (_currentMode == Mode.ConditionPanel) SubmitConditionPanel();
        else                                     SubmitSsq();
    }

    private void SubmitConditionPanel()
    {
        // Integrity gate (CQ 2-6): fail LOUDLY rather than write dimensions into the wrong fields.
        // The single-source NASA mapping must still match NasaScores, and the built panel must
        // carry exactly 6 NASA items per task — otherwise the positional Assign would misalign.
        int idNasa = 0, asNasa = 0;
        for (int i = 0; i < _panelItems.Count; i++)
        {
            if (_panelItems[i].Construct != PanelConstruct.Nasa) continue;
            if (_panelItems[i].Task == PanelTask.Identification) idNasa++;
            else if (_panelItems[i].Task == PanelTask.Assembly)  asNasa++;
        }
        if (s_nasaDimensions.Length != k_nasaScoresFieldCount || _answers.Length != _panelItems.Count ||
            idNasa != k_nasaScoresFieldCount || asNasa != k_nasaScoresFieldCount)
        {
            string msg = $"Condition-panel mapping mismatch: dims={s_nasaDimensions.Length}, " +
                         $"fields={k_nasaScoresFieldCount}, idNasa={idNasa}, asNasa={asNasa}, " +
                         $"items={_panelItems.Count}, answers={_answers.Length}. Aborting save.";
            Debug.LogError($"[QuestionnaireManager] {msg}");
            FileLogger.Log("Questionnaire", $"ABORT {msg}");
            ShowSaveError("dimension mapping mismatch");
            return;
        }

        // Route each answer from the SINGLE source of truth so label↔field stays aligned.
        var idScores = new NasaScores();
        var asScores = new NasaScores();
        var gazeRows = new List<GazeItemEntry>();

        for (int i = 0; i < _panelItems.Count; i++)
        {
            var item = _panelItems[i];
            int ans  = _answers[i];
            if (item.Construct == PanelConstruct.Nasa)
            {
                s_nasaDimensions[item.NasaDimIndex].Assign(
                    item.Task == PanelTask.Identification ? idScores : asScores, ans);
            }
            else
            {
                gazeRows.Add(new GazeItemEntry
                {
                    condition_index = _conditionIndex,
                    condition_name  = _conditionName,
                    task_type       = TaskTypeString(item.Task),
                    construct       = ConstructString(item.Construct),
                    score           = ans,
                    missing         = false,
                });
            }
        }

        // NoGaze: usefulness (both tasks) + accuracy MC are structurally absent — record them
        // explicitly as missing so the dataset is rectangular; never imputed.
        if (_panelIsNoGaze)
        {
            gazeRows.Add(MakeMissingGazeRow(PanelTask.Identification, "usefulness"));
            gazeRows.Add(MakeMissingGazeRow(PanelTask.Assembly,       "usefulness"));
            gazeRows.Add(MakeMissingGazeRow(PanelTask.Condition,      "accuracy_mc"));
        }

        // Append, then persist. On failure, roll back EVERYTHING added this round so a retry
        // does not duplicate, leave the panel up with a visible error, and DO NOT advance.
        int nasaStart = _data.nasa_tlx.Count;
        int gazeStart = _data.gaze_items.Count;

        _data.nasa_tlx.Add(new NasaTlxEntry
        {
            condition_index = _conditionIndex, condition_name = _conditionName,
            task_type = "identification", scores = idScores,
        });
        _data.nasa_tlx.Add(new NasaTlxEntry
        {
            condition_index = _conditionIndex, condition_name = _conditionName,
            task_type = "assembly", scores = asScores,
        });
        _data.gaze_items.AddRange(gazeRows);

        if (!SaveJson())
        {
            _data.nasa_tlx.RemoveRange(nasaStart, _data.nasa_tlx.Count - nasaStart);
            _data.gaze_items.RemoveRange(gazeStart, _data.gaze_items.Count - gazeStart);
            ShowSaveError("local write failed");
            return;
        }

        SetVisible(false);
        _currentMode = Mode.None;
        photonView.RPC(nameof(RPC_QuestionnaireComplete), RpcTarget.All);
    }

    private GazeItemEntry MakeMissingGazeRow(PanelTask task, string construct) => new GazeItemEntry
    {
        condition_index = _conditionIndex,
        condition_name  = _conditionName,
        task_type       = TaskTypeString(task),
        construct       = construct,
        score           = -1,
        missing         = true,
    };

    private static string TaskTypeString(PanelTask task) => task switch
    {
        PanelTask.Identification => "identification",
        PanelTask.Assembly       => "assembly",
        _                        => "condition",
    };

    private static string ConstructString(PanelConstruct c) => c switch
    {
        PanelConstruct.Comprehension => "comprehension",
        PanelConstruct.Usefulness    => "usefulness",
        PanelConstruct.AccuracyMC    => "accuracy_mc",
        _                            => "nasa",
    };

    private void SubmitSsq()
    {
        int total = 0;
        foreach (int v in _answers) total += v;

        _data.ssq = new SsqData
        {
            scores = (int[])_answers.Clone(),
            total  = total
        };

        // SSQ is overwritten (not accumulated) so a retry is idempotent; on save failure
        // halt with a visible error and DO NOT advance, protecting the final dataset.
        if (!SaveJson())
        {
            ShowSaveError("local write failed");
            return;
        }

        Debug.Log($"[QuestionnaireManager] SSQ submitted. Total={total}. Path={_saveFilePath}");

        SetVisible(false);
        _currentMode = Mode.None;
        photonView.RPC(nameof(RPC_QuestionnaireComplete), RpcTarget.All);
    }

    private void ShowSaveError(string detail)
    {
        Debug.LogError($"[QuestionnaireManager] SAVE ERROR surfaced to operator: {detail}");
        if (_titleText != null)
        {
            _titleText.color = new Color(1f, 0.45f, 0.45f);
            _titleText.text  = "⚠ 保存に失敗しました — もう一度［送信］を押してください\nSAVE FAILED — press Submit again";
        }
        if (_nextButtonLabel != null) _nextButtonLabel.text = "再送信 / Retry ⟳";
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

    private bool SaveJson()
    {
        EnsureSavePath();

        // Re-sync participant_id in case it was set after Awake.
        _data.participant_id = !string.IsNullOrEmpty(participantId) ? participantId : $"P{participantNumber:D2}";

        string json = JsonUtility.ToJson(_data, prettyPrint: true);

        // Local save (HMD backup). Write a temp file first, then publish atomically (2-5):
        // a crash mid-write can only ever damage the throwaway .tmp, never the live results.
        bool localOk = false;
        try
        {
            string tmpPath = _saveFilePath + ".tmp";
            File.WriteAllText(tmpPath, json);

            if (File.Exists(_saveFilePath))
            {
                // File.Replace is atomic on NTFS and keeps a .bak of the prior good copy.
                // Some Android/Mono filesystem backends throw on it, so fall back to the
                // (proven) delete+move path rather than ever leaving data unsaved.
                try
                {
                    File.Replace(tmpPath, _saveFilePath, _saveFilePath + ".bak");
                }
                catch (Exception rex)
                {
                    Debug.LogWarning($"[QuestionnaireManager] File.Replace unsupported here ({rex.Message}); using delete+move fallback.");
                    if (File.Exists(_saveFilePath)) File.Delete(_saveFilePath);
                    File.Move(tmpPath, _saveFilePath);
                }
            }
            else
            {
                File.Move(tmpPath, _saveFilePath);   // first save — nothing to replace
            }

            localOk = true;
            Debug.Log($"[QuestionnaireManager] JSON saved → {_saveFilePath}");
            FileLogger.Log("Questionnaire", $"SAVE OK ({_currentMode}) → {_saveFilePath}");
        }
        catch (Exception ex)
        {
            // Persistent, operator-visible record of the failure (CQ11): converts a silent
            // data-loss into something detectable in the run log.
            Debug.LogError($"[QuestionnaireManager] Local save FAILED: {ex.Message}");
            FileLogger.Log("Questionnaire", $"SAVE FAILED ({_currentMode}) → {_saveFilePath} : {ex.Message}");
        }

        // Forward to Expert (PC) so the researcher can access the data without ADB. Best-effort
        // backup — attempted even if the local save failed, so the data isn't solely on the HMD.
        if (PhotonNetwork.InRoom && photonView != null)
            photonView.RPC(nameof(RPC_ForwardQuestionnaireJson), RpcTarget.Others, json, _data.participant_id);

        return localOk;
    }

    // ─── Photon RPC ──────────────────────────────────────────────────────────

    [PunRPC]
    private void RPC_QuestionnaireComplete()
    {
        Debug.Log("[QuestionnaireManager] RPC_QuestionnaireComplete received.");
        OnQuestionnaireComplete?.Invoke();
        OnSurveySubmitted?.Invoke();   // Agent 1 (ExperimentManager2) gates the Finished screen on this
    }

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
            FileLogger.Log("Questionnaire", $"PC COPY OK → {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[QuestionnaireManager] PC save failed: {ex.Message}");
            FileLogger.Log("Questionnaire", $"PC COPY FAILED → {path} : {ex.Message}");
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
