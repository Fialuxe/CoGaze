using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;

public class WorkerHUD2 : MonoBehaviour
{
    [Header("Optional - assign a font with Japanese glyphs")]
    public Font japaneseFont;

    private void Awake()
    {
        if (japaneseFont != null) return;
        japaneseFont = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
        if (japaneseFont == null) japaneseFont = Resources.Load<Font>("Fonts/NotoSansCJK-Regular");
        if (japaneseFont == null) japaneseFont = Resources.Load<Font>("Fonts/NotoSansJP");
        if (japaneseFont != null)
            Debug.Log($"[WorkerHUD2] Japanese font auto-loaded: {japaneseFont.name}");
    }

    [Header("HUD position relative to center eye (metres)")]
    public Vector3 hudOffset  = new Vector3(-0.30f, 0.3f, 0.7f);
    public Vector2 hudSizeMm  = new Vector2(240f, 92f);
    public float   hudScaleM  = 0.001f;

    [Header("Alert Marker")]
    public float alertDistance  = 1.0f;
    public float lookAtAngleDeg = 15f;

    private Image backgroundImage;
    private Text  connStatusText;
    private Text  stateText;
    private Text  timerText;

    private GameObject alertMarkerGo;
    private bool       alertActive        = false;
    private bool       taskTimerExpired   = false;
    private float      alertActivatedTime = -1f;

    // ── Breathing guide ───────────────────────────────────────────────────
    private GameObject _breathGo;
    private Image      _breathDisc;
    private Text       _breathInstr;
    private Text       _breathCond;
    private Text       _breathCountdown;
    private Texture2D  _discTex;
    private float      _breathPhase;
    private bool       _breathingActive;
    private const float BreathCycle = 8f;

    private Transform          cameraAnchor;
    private ExperimentManager2 manager;

    // UX: calibration indicator
    private Text  _calibText;
    private bool  _calibTutorialShown = false;

    // Kept so OnDestroy can unsubscribe: these publishers (MeshHandler / IdentificationTask)
    // outlive this HUD across a reconnect, so a leaked handler would fire into a dead object.
    private MeshHandler        _meshHandler;
    private IdentificationTask _idTask;
    private System.Action<bool> _qrHandler;

    public void Initialize(ExperimentManager2 experimentManager)
    {
        manager = experimentManager;
        BuildHUD();

        manager.OnStateChanged       += HandleStateChanged;
        manager.OnInstructionChanged += HandleInstructionChanged;
        manager.OnProgressChanged    += HandleProgressChanged;

        if (manager != null)
            HandleStateChanged(manager.CurrentState);
    }

    /// <summary>
    /// Wire up calibration feedback. Call after Initialize() once MeshHandler is available.
    /// UX improvement 1: HUD row, 2: haptic confirmation.
    /// </summary>
    public void ConnectMeshHandler(MeshHandler meshHandler)
    {
        if (meshHandler == null) return;
        _meshHandler = meshHandler;
        meshHandler.OnCalibrationChanged += OnCalibrationChanged;
    }

    private void OnCalibrationChanged(bool active)
    {
        if (_calibText == null) return;
        _calibText.gameObject.SetActive(active);

        if (active)
        {
            if (!_calibTutorialShown)
            {
                _calibTutorialShown = true;
                StartCoroutine(CycleCalibrateHints());
            }
            else
            {
                _calibText.text = CoGazeStrings.Calib_FullHint;
            }
        }
    }

    private System.Collections.IEnumerator CycleCalibrateHints()
    {
        if (_calibText == null) yield break;
        string[] hints =
        {
            CoGazeStrings.Calib_MoveXZ,
            CoGazeStrings.Calib_AdjustHeight,
            CoGazeStrings.Calib_Rotate,
            CoGazeStrings.Calib_Confirm,
        };
        foreach (var h in hints)
        {
            _calibText.text = h;
            yield return new WaitForSeconds(2f);
        }
        _calibText.text = CoGazeStrings.Calib_FullHint;
    }

    /// <summary>
    /// UX improvement 2: haptic pulse + flash "送信完了" on confirm.
    /// Call from LocalWorkerSetup after ConnectMeshHandler.
    /// </summary>
    public void OnCalibrationConfirmed()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        OVRInput.SetControllerVibration(0.5f, 0.8f, OVRInput.Controller.RTouch);
        StartCoroutine(StopVibration(0.2f));
#endif
        if (_calibText != null)
            StartCoroutine(FlashConfirm());
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private System.Collections.IEnumerator StopVibration(float delay)
    {
        yield return new WaitForSeconds(delay);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
    }
#endif

    private System.Collections.IEnumerator FlashConfirm()
    {
        if (_calibText == null) yield break;
        Color orig = _calibText.color;
        _calibText.text  = CoGazeStrings.Calib_Sent;
        _calibText.color = new Color(0.3f, 1f, 0.5f);
        yield return new WaitForSeconds(1.5f);
        _calibText.text  = CoGazeStrings.Calib_FullHint;
        _calibText.color = orig;
    }

    /// <summary>
    /// UX improvement 3: update state text based on identification task QR scan state.
    /// </summary>
    public void ConnectIdentificationTask(IdentificationTask task)
    {
        if (task == null) return;
        _idTask = task;
        // Stored in a field (not an inline lambda) so OnDestroy can unsubscribe it.
        _qrHandler = qrFound =>
        {
            if (manager == null || manager.CurrentState != ExperimentState.TaskRunning) return;
            if (qrFound)
                SetState(CoGazeStrings.Worker_QRFound, new Color(0.3f, 1f, 0.5f));
            else
                SetState(CoGazeStrings.Worker_QRSearching, new Color(0.6f, 0.9f, 1f));
        };
        task.OnQRStateChanged += _qrHandler;
    }

    private void OnDestroy()
    {
        if (manager != null)
        {
            manager.OnStateChanged       -= HandleStateChanged;
            manager.OnInstructionChanged -= HandleInstructionChanged;
            manager.OnProgressChanged    -= HandleProgressChanged;
        }
        // MeshHandler/IdentificationTask survive a HUD teardown (e.g. reconnect), so leaving
        // these subscribed would leak and double-fire into the destroyed HUD.
        if (_meshHandler != null) _meshHandler.OnCalibrationChanged -= OnCalibrationChanged;
        if (_idTask != null && _qrHandler != null) _idTask.OnQRStateChanged -= _qrHandler;
        if (alertMarkerGo != null) Destroy(alertMarkerGo);
        if (_discTex != null) Destroy(_discTex);
    }

    private void Update()
    {
        if (manager == null) return;

        RefreshConnectionStatus();
        RefreshTimer();
        RefreshAlertBillboard();
        if (_breathingActive) AnimateBreathing();
    }

    private void RefreshConnectionStatus()
    {
        if (connStatusText == null) return;

        if (!PhotonNetwork.IsConnected)
        {
            connStatusText.text  = CoGazeStrings.Worker_ConnDisconnected;
            connStatusText.color = Color.red;
            return;
        }

        bool expertOnline = false;
        foreach (var p in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(p) == RoleManager.ROLE_EXPERT)
            { expertOnline = true; break; }
        }

        if (expertOnline)
        {
            connStatusText.text  = CoGazeStrings.Worker_ConnExpertOnline;
            connStatusText.color = new Color(0.3f, 1f, 0.5f);
        }
        else
        {
            connStatusText.text  = CoGazeStrings.Worker_ConnExpertWaiting;
            connStatusText.color = Color.yellow;
        }
    }

    private void RefreshTimer()
    {
        if (timerText == null || manager == null) return;

        float rem   = manager.RemainingSeconds;
        var   state = manager.CurrentState;

        switch (state)
        {
            case ExperimentState.WhiteNoise:
                if (_breathCountdown != null)
                    _breathCountdown.text = $"あと {FormatTime(rem)}";
                break;

            case ExperimentState.TaskRunning:
                timerText.text  = FormatTime(rem);
                timerText.color = rem < 5f ? Color.red : Color.white;

                if (rem <= 0f && !taskTimerExpired)
                {
                    taskTimerExpired = true;
                    ShowAlert();
                }
                break;

            default:
                break;
        }
    }

    private void RefreshAlertBillboard()
    {
        if (!alertActive || cameraAnchor == null || alertMarkerGo == null) return;
        if (Time.time - alertActivatedTime < 0.5f) return;

        Vector3 d = (cameraAnchor.position - alertMarkerGo.transform.position).normalized;
        if (d != Vector3.zero)
            alertMarkerGo.transform.rotation = Quaternion.LookRotation(d, Vector3.up);

        Vector3 toMarker = (alertMarkerGo.transform.position - cameraAnchor.position).normalized;
        if (Vector3.Angle(cameraAnchor.forward, toMarker) < lookAtAngleDeg)
            DismissAlert();
    }

    private void BuildHUD()
    {
#pragma warning disable CS0618
        var rig = Object.FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618
        cameraAnchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
        if (cameraAnchor == null)
        {
            Debug.LogWarning("[WorkerHUD2] No camera anchor found - HUD will not be shown.");
            return;
        }

        var go = new GameObject("WorkerHUD2_Canvas");
        go.transform.SetParent(cameraAnchor, false);
        go.transform.localPosition = hudOffset;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one * hudScaleM;

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        go.GetComponent<RectTransform>().sizeDelta = hudSizeMm;

        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(go.transform, false);
        backgroundImage = bgGo.AddComponent<Image>();
        backgroundImage.color = new Color(0.04f, 0.06f, 0.20f, 0.50f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        var accent    = new GameObject("Accent");
        accent.transform.SetParent(go.transform, false);
        var accentImg = accent.AddComponent<Image>();
        accentImg.color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var art = accent.GetComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = new Vector2(0f, 1f);
        art.offsetMin = Vector2.zero; art.offsetMax = new Vector2(4f, 0f);

        connStatusText = MakeText("ConnStatus", go.transform,
            new Vector2(0.05f, 0.74f), new Vector2(0.98f, 0.98f),
            CoGazeStrings.Worker_ConnChecking, 20, TextAnchor.MiddleLeft, Color.yellow);

        stateText = MakeText("StateText", go.transform,
            new Vector2(0.05f, 0.38f), new Vector2(0.98f, 0.76f),
            CoGazeStrings.Worker_StateIdle, 22, TextAnchor.MiddleLeft, new Color(0.6f, 0.9f, 1f));

        timerText = MakeText("TimerText", go.transform,
            new Vector2(0.05f, 0.02f), new Vector2(0.98f, 0.40f),
            CoGazeStrings.Worker_TimerEmpty, 28, TextAnchor.MiddleCenter, Color.white);

        MakeDivider(go.transform, 0.375f);
        MakeDivider(go.transform, 0.74f);

        // Calibration mode indicator — hidden until grip toggles calibration ON
        _calibText = MakeText("CalibStatus", go.transform,
            new Vector2(0.0f, -0.12f), new Vector2(1.0f, 0.0f),
            "", 18, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.2f));
        _calibText.gameObject.SetActive(false);

        BuildAlertMarker();
        BuildBreathingGuide();

        Debug.Log("[WorkerHUD2] HUD built successfully.");
    }

    private void MakeDivider(Transform parent, float yAnchor)
    {
        var go  = new GameObject("Divider");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.3f, 0.75f, 1f, 0.25f);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.03f, yAnchor);
        rt.anchorMax = new Vector2(0.97f, yAnchor);
        rt.offsetMin = new Vector2(0f, -0.5f);
        rt.offsetMax = new Vector2(0f,  0.5f);
    }

    private void BuildAlertMarker()
    {
        if (cameraAnchor == null) return;
        alertMarkerGo = new GameObject("WorkerHUD2_AlertMarker");
        alertMarkerGo.transform.position = cameraAnchor.position + cameraAnchor.forward;

        var mc = alertMarkerGo.AddComponent<Canvas>();
        mc.renderMode = RenderMode.WorldSpace;
        alertMarkerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(160f, 160f);
        alertMarkerGo.transform.localScale = Vector3.one * 0.002f;

        var bgGo = new GameObject("AlertBG");
        bgGo.transform.SetParent(alertMarkerGo.transform, false);
        bgGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.70f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        MakeText("Excl", alertMarkerGo.transform,
            new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f),
            CoGazeStrings.Worker_AlertExclamation, 110, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.1f));

        alertMarkerGo.SetActive(false);
    }

    private void HandleStateChanged(ExperimentState state)
    {
        HideBreathGuide();

        if (state != ExperimentState.TaskRunning)
        {
            taskTimerExpired = false;
            DismissAlert();
        }

        switch (state)
        {
            case ExperimentState.Idle:
                SetState(CoGazeStrings.Worker_Idle, new Color(1f, 0.85f, 0.2f));
                SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.Ready:
                SetState(CoGazeStrings.Worker_Ready, Color.green);
                SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.WhiteNoise:
                SetPanelMode(false);
                ShowBreathGuide();
                break;

            case ExperimentState.TaskRunning:
                SetState(CoGazeStrings.Worker_TaskRunning, new Color(0.6f, 0.9f, 1f));
                SetTimer(FormatTime(manager.RemainingSeconds), Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.TaskComplete:
                SetState(CoGazeStrings.Worker_TaskComplete, new Color(1f, 0.65f, 0.15f));
                SetTimer(CoGazeStrings.Worker_TimerZero, Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.NoiseComplete:
                SetState(CoGazeStrings.Worker_NoiseComplete, new Color(1f, 0.65f, 0.15f));
                SetTimer(CoGazeStrings.Worker_TimerZero, Color.yellow);
                SetPanelMode(true);
                break;

            case ExperimentState.Questionnaire:
                SetState(CoGazeStrings.Worker_Questionnaire, new Color(0.4f, 0.8f, 1f));
                SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.Finished:
                SetState(CoGazeStrings.Worker_Finished, Color.cyan);
                SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.gray);
                SetPanelMode(true);
                break;
        }
    }

    private void HandleProgressChanged(int stepIdx, int totalSteps, StepType stepType)
    {
        var state = manager != null ? manager.CurrentState : ExperimentState.Idle;
        if (state == ExperimentState.TaskComplete ||
            state == ExperimentState.NoiseComplete ||
            state == ExperimentState.Finished)
            return;

        int runPos    = manager != null ? manager.CurrentConditionRunPosition : -1;
        int condTotal = ExperimentDesign.Conditions.Length;
        string condLabel = runPos >= 0 ? $"[条件 {runPos + 1}/{condTotal}] " : "";
        bool noGaze = manager != null && manager.CurrentGazeMode == GazeMode.None;

        switch (stepType)
        {
            case StepType.Noise:
                SetState(condLabel + CoGazeStrings.Worker_NoiseInProgress, Color.yellow);
                break;

            case StepType.Task:
            {
                // NoGaze conditions need a different message; for all gaze modes prefer the authored
                // [local] instruction from the template file (includes "Done ボタンを押して" etc.).
                string fileInstr = !noGaze && manager != null ? manager.GetInstruction(stepIdx) : null;
                string taskText  = !string.IsNullOrEmpty(fileInstr)
                    ? condLabel + fileInstr
                    : condLabel + (noGaze
                        ? CoGazeStrings.Worker_TaskNoGaze
                        : CoGazeStrings.Worker_TaskWithGaze);
                SetState(taskText, new Color(0.6f, 0.9f, 1f));
                break;
            }

            case StepType.Assembly:
            {
                string fileInstr    = !noGaze && manager != null ? manager.GetInstruction(stepIdx) : null;
                string assemblyText = !string.IsNullOrEmpty(fileInstr)
                    ? condLabel + fileInstr
                    : condLabel + (noGaze
                        ? CoGazeStrings.Worker_AssemblyNoGaze
                        : CoGazeStrings.Worker_AssemblyWithGaze);
                SetState(assemblyText, new Color(0.6f, 0.9f, 1f));
                break;
            }

            case StepType.Questionnaire:
                SetState(CoGazeStrings.Worker_QuestionnaireStep, new Color(0.4f, 0.8f, 1f));
                break;

            case StepType.ConditionStart:
            {
                // Template LocalInstruction already includes "【条件 X/10】" — no condLabel prefix.
                string fileInstrCS = manager != null ? manager.GetInstruction(stepIdx) : null;
                string csText = !string.IsNullOrEmpty(fileInstrCS)
                    ? fileInstrCS
                    : (runPos >= 0 ? $"条件 {runPos + 1}/{condTotal}" : CoGazeStrings.Worker_ConditionNextLabel) + CoGazeStrings.Worker_ConditionStartSuffix;
                SetState(csText, new Color(0.6f, 1f, 0.6f));
                break;
            }
        }
    }

    private void HandleInstructionChanged(string instruction)
    {
        if (!string.IsNullOrEmpty(instruction))
            SetState(instruction, new Color(0.6f, 0.9f, 1f));
    }

    private void SetPanelMode(bool full)
    {
        if (backgroundImage != null)
            backgroundImage.color = full
                ? new Color(0.04f, 0.06f, 0.20f, 0.50f)
                : new Color(0.02f, 0.02f, 0.08f, 0.40f);

        if (stateText      != null) stateText.enabled      = full;
        if (connStatusText != null) connStatusText.enabled  = full;
    }

    private void ShowAlert()
    {
        if (alertMarkerGo == null || cameraAnchor == null) return;

        Vector3 fwd   = Vector3.ProjectOnPlane(cameraAnchor.forward, Vector3.up).normalized;
        if (fwd == Vector3.zero) fwd = Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

        Vector3 dir = (fwd + right * Mathf.Tan(40f * Mathf.Deg2Rad)).normalized;
        dir = (dir + Vector3.up * Mathf.Tan(10f * Mathf.Deg2Rad)).normalized;

        alertMarkerGo.transform.position = cameraAnchor.position + dir * alertDistance;
        Vector3 d = (cameraAnchor.position - alertMarkerGo.transform.position).normalized;
        if (d != Vector3.zero)
            alertMarkerGo.transform.rotation = Quaternion.LookRotation(d, Vector3.up);

        alertActive        = true;
        alertActivatedTime = Time.time;
        alertMarkerGo.SetActive(true);
    }

    private void DismissAlert()
    {
        if (alertMarkerGo != null) alertMarkerGo.SetActive(false);
        alertActive = false;
    }

    private void SetState(string text, Color color)
    {
        if (stateText == null) return;
        stateText.text  = text;
        stateText.color = color;
    }

    private void SetTimer(string text, Color color)
    {
        if (timerText == null) return;
        timerText.text  = text;
        timerText.color = color;
    }

    private static string FormatTime(float s)
    {
        s = Mathf.Max(0f, s);
        return $"{(int)(s / 60f):D2}:{(int)(s % 60f):D2}";
    }

    private void StretchFill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    // ── Breathing guide ───────────────────────────────────────────────────────

    private void BuildBreathingGuide()
    {
        if (cameraAnchor == null) return;

        _breathGo = new GameObject("BreathingGuide");
        _breathGo.transform.SetParent(cameraAnchor, false);
        _breathGo.transform.localPosition = new Vector3(0f, 0f, 1.0f);
        _breathGo.transform.localRotation = Quaternion.identity;
        _breathGo.transform.localScale    = Vector3.one * hudScaleM;

        var canvas = _breathGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _breathGo.GetComponent<RectTransform>().sizeDelta = new Vector2(260f, 200f);

        // Background
        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(_breathGo.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0.04f, 0.06f, 0.18f, 0.88f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        // Accent bar top
        var topBar = new GameObject("TopBar");
        topBar.transform.SetParent(_breathGo.transform, false);
        topBar.AddComponent<Image>().color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var tbrt = topBar.GetComponent<RectTransform>();
        tbrt.anchorMin = new Vector2(0f, 0.88f); tbrt.anchorMax = new Vector2(1f, 0.88f);
        tbrt.offsetMin = new Vector2(0f, -1.5f); tbrt.offsetMax = new Vector2(0f, 1.5f);

        // Condition label (top)
        _breathCond = MakeText("CondLabel", _breathGo.transform,
            new Vector2(0.05f, 0.88f), new Vector2(0.95f, 1.00f),
            "", 26, TextAnchor.MiddleCenter, new Color(0.5f, 1f, 0.65f));

        // Breathing disc (centered upper area)
        _discTex = CreateDiscTexture(64);
        var discGo = new GameObject("BreathDisc");
        discGo.transform.SetParent(_breathGo.transform, false);
        _breathDisc = discGo.AddComponent<Image>();
        _breathDisc.sprite = Sprite.Create(_discTex, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f));
        _breathDisc.color  = new Color(0.2f, 0.85f, 0.65f, 0.90f);
        var drt = discGo.GetComponent<RectTransform>();
        drt.anchorMin = new Vector2(0.5f, 0.5f);
        drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.pivot     = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(72f, 72f);
        drt.anchoredPosition = new Vector2(0f, 22f);

        // Breathing instruction (below disc)
        _breathInstr = MakeText("BreathInstr", _breathGo.transform,
            new Vector2(0.05f, 0.31f), new Vector2(0.95f, 0.54f),
            CoGazeStrings.Worker_BreathIn, 26, TextAnchor.MiddleCenter, new Color(0.88f, 0.94f, 1f));

        // Countdown (bottom)
        _breathCountdown = MakeText("BreathCountdown", _breathGo.transform,
            new Vector2(0.05f, 0.02f), new Vector2(0.95f, 0.24f),
            CoGazeStrings.Worker_BreathCountdownEmpty, 20, TextAnchor.MiddleCenter, new Color(0.55f, 0.65f, 0.75f));

        _breathGo.SetActive(false);
    }

    private Texture2D CreateDiscTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - r, y + 0.5f - r).magnitude;
                float a = Mathf.Clamp01((r - 1f - d) / 2.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return tex;
    }

    private void ShowBreathGuide()
    {
        int runPos    = manager != null ? manager.CurrentConditionRunPosition : -1;
        int condTotal = ExperimentDesign.Conditions.Length;

        if (_breathCond != null)
            _breathCond.text = runPos >= 0
                ? $"条件  {runPos + 1} / {condTotal}  [{GazeModeLabel(manager.CurrentGazeMode)}]"
                : CoGazeStrings.Worker_BreathIntervalLabel;

        _breathPhase    = 0f;
        _breathingActive = true;

        if (_breathGo != null) _breathGo.SetActive(true);
        if (timerText  != null) timerText.enabled = false;
    }

    private void HideBreathGuide()
    {
        _breathingActive = false;
        if (_breathGo  != null) _breathGo.SetActive(false);
        if (timerText  != null) timerText.enabled = true;
    }

    private void AnimateBreathing()
    {
        _breathPhase = (_breathPhase + Time.deltaTime / BreathCycle) % 1f;

        bool  inhale = _breathPhase < 0.5f;
        float t      = inhale ? (_breathPhase / 0.5f) : ((_breathPhase - 0.5f) / 0.5f);
        float smooth = t * t * (3f - 2f * t);   // smoothstep
        float scale  = Mathf.Lerp(inhale ? 0.75f : 1.25f, inhale ? 1.25f : 0.75f, smooth);

        if (_breathDisc != null)
        {
            _breathDisc.GetComponent<RectTransform>().localScale = Vector3.one * scale;

            Color teal = new Color(0.20f, 0.85f, 0.65f, 0.90f);
            Color blue = new Color(0.35f, 0.55f, 1.00f, 0.90f);
            _breathDisc.color = Color.Lerp(inhale ? teal : blue, inhale ? blue : teal, smooth * 0.25f);
        }

        if (_breathInstr != null)
            _breathInstr.text = inhale ? CoGazeStrings.Worker_BreathIn : CoGazeStrings.Worker_BreathOut;
    }

    private static string GazeModeLabel(GazeMode m) => m == GazeMode.None ? "NoGaze" : m.ToString();

    // ── Text / panel helpers ──────────────────────────────────────────────────

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
}
