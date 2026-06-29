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
    // 1.2 m: within Quest lens focal range (~1.3–2.0 m); closer causes eye strain.
    // +0.21 m: workpiece is 20–40° below gaze, so HUD above avoids visual competition.
    // −0.21 m: slight left offset keeps forward view clear; reachable with a small eye movement.
    public Vector3 hudOffset  = new Vector3(-0.21f, 0.21f, 1.2f);
    public Vector2 hudSizeMm  = new Vector2(520f, 200f);            // taller panel for merged setup content
    public float   hudScaleM  = 0.001f;

    [Header("HUD comfort-follow (UX6)")]
    [Tooltip("How quickly the world-space HUD eases toward its comfortable spot. Higher = snappier.")]
    public float   hudFollowLerp = 6f;
    // UX6: the task HUD is no longer rigidly parented to the head. It lives in world space and
    // follows head POSITION + YAW only (not pitch/roll), so looking down to assemble no longer
    // drags the panel across the work area.
    private Transform _hudCanvas;

    [Header("Alert Marker")]
    public float alertDistance  = 1.0f;
    public float lookAtAngleDeg = 15f;

    private Image _backgroundImage;
    private Text  _connStatusText;
    private Text  _stateText;
    private Text  _timerText;

    private GameObject _alertMarkerGo;
    private bool       _alertActive;
    private bool       _taskTimerExpired;
    private float      _alertActivatedTime = -1f;

    // ── Breathing guide ───────────────────────────────────────────────────
    private GameObject _breathGo;
    private Image      _breathDisc;
    private Text       _breathInstr;
    private Text       _breathCond;
    private Text       _breathCountdown;
    private Texture2D  _discTex;
    private float      _breathPhase;
    private bool       _breathingActive;
    private const float k_breathCycle = 8f;

    private Transform          _cameraAnchor;
    private ExperimentManager2 _manager;

    // UX: calibration indicator
    private Text  _calibText;
    private bool  _calibTutorialShown;

    // Setup state rows — shown only during ExperimentState.Setup; replace _stateText/_timerText.
    // Filled by SetupCoordinator via UpdateSetupStatus().
    private Text _setupCalibText;
    private Text _setupTaskText;
    private Text _setupHintText;

    // Kept so OnDestroy can unsubscribe: these publishers (MeshHandler / IdentificationTask)
    // outlive this HUD across a reconnect, so a leaked handler would fire into a dead object.
    private MeshHandler        _meshHandler;
    private IdentificationTask _idTask;
    private System.Action<bool>       _qrHandler;
    private System.Action             _idDoneHandler;     // OnCorrectGrip → haptic + flash
    private System.Action<string,int> _idScoreHandler;    // OnTargetChanged → score display
    private System.Action<float, float> _outlierHandler;

    // Countdown (3-2-1-GO) shown once after the first Setup approval
    private Text               _countdownText;
    private ExperimentState    _prevState = ExperimentState.Setup;

    // Gaze availability indicator
    private Text           _gazeStatusText;
    private GazeVisualizer _gazeVisualizer;
    private bool           _lastGazeAvailable = false;

    public void Initialize(ExperimentManager2 experimentManager)
    {
        _manager = experimentManager;
        BuildHUD();
        _gazeVisualizer = FindAnyObjectByType<GazeVisualizer>();

        _manager.OnStateChanged       += HandleStateChanged;
        _manager.OnInstructionChanged += HandleInstructionChanged;
        _manager.OnProgressChanged    += HandleProgressChanged;
        _manager.OnCountdownTick      += HandleCountdownTick;

        if (_manager != null)
            HandleStateChanged(_manager.CurrentState);
    }

    public void ConnectMeshHandler(MeshHandler meshHandler)
    {
        if (meshHandler == null) return;
        _meshHandler = meshHandler;
        meshHandler.OnCalibrationChanged   += OnCalibrationChanged;
        meshHandler.OnDualQRCalibStep      += OnDualQRCalibStep;
        // Bridge the confirm event to the (previously caller-less) OnCalibrationConfirmed() so the
        // haptic + "送信完了" flash actually fire. Subscribed here (not from SceneBootstrapper2) so
        // OnDestroy can unsubscribe it — MeshHandler outlives this HUD across a reconnect.
        meshHandler.OnCalibrationConfirmed += OnCalibrationConfirmed;

        // Show a visible warning when dual-QR outlier rejection fires so the operator immediately
        // knows that the physical QR separation doesn't match the indicator setup.
        _outlierHandler = (measured, expected) =>
        {
            if (_calibText == null) return;
            _calibText.gameObject.SetActive(true);
            _calibText.color = Color.red;
            _calibText.text  = $"⚠ QR間隔が合いません\n実測 {measured:F2}m / 期待 {expected:F2}m";
            StopCoroutine(nameof(ClearOutlierWarning));
            StartCoroutine(nameof(ClearOutlierWarning));
        };
        meshHandler.OnDualQROutlierRejected += _outlierHandler;

        // Immediately show initial dual-QR step so the Worker knows what to do from app launch.
        if (meshHandler.IsDualQRMode && _calibText != null)
        {
            _calibText.gameObject.SetActive(true);
            OnDualQRCalibStep(meshHandler.CurrentDualCalibState);
        }
    }

    private System.Collections.IEnumerator ClearOutlierWarning()
    {
        yield return new WaitForSeconds(4f);
        if (_calibText == null) yield break;
        _calibText.color = new Color(1f, 0.85f, 0.2f);
        if (_meshHandler != null && _meshHandler.IsDualQRMode)
            OnDualQRCalibStep(_meshHandler.CurrentDualCalibState);
    }

    private void OnDualQRCalibStep(DualQRCalibState step)
    {
        if (_calibText == null) return;
        string cA = _meshHandler?.CalibQRColorA ?? "赤色の枠";
        string cB = _meshHandler?.CalibQRColorB ?? "青色の枠";
        switch (step)
        {
            case DualQRCalibState.NeedsA:
                _calibText.gameObject.SetActive(true);
                _calibText.text = CoGazeStrings.DualCalib_NeedsA(cA);
                break;
            case DualQRCalibState.NeedsB:
                _calibText.gameObject.SetActive(true);
                _calibText.text = CoGazeStrings.DualCalib_NeedsB(cA, cB);
                break;
            case DualQRCalibState.Complete:
                StartCoroutine(FlashDualCalibComplete());
                break;
        }
    }

    private System.Collections.IEnumerator FlashDualCalibComplete()
    {
        if (_calibText == null) yield break;
        Color orig = _calibText.color;
        _calibText.gameObject.SetActive(true);
        _calibText.text  = CoGazeStrings.DualCalib_Complete;
        _calibText.color = new Color(0.3f, 1f, 0.5f);
#if UNITY_ANDROID && !UNITY_EDITOR
        OVRInput.SetControllerVibration(0.5f, 1.0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0.5f, 1.0f, OVRInput.Controller.RTouch);
        yield return new WaitForSeconds(0.3f);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
#else
        yield return new WaitForSeconds(0.3f);
#endif
        yield return new WaitForSeconds(2.0f);
        _calibText.color = orig;
        _calibText.gameObject.SetActive(false);
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

    public void OnCalibrationConfirmed()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        OvrHaptics.Pulse(this, 0.5f, 0.8f, 0.2f, OVRInput.Controller.RTouch);
#endif
        if (_calibText != null)
            StartCoroutine(FlashConfirm());
    }


    private System.Collections.IEnumerator FlashConfirm()
    {
        if (_calibText == null) yield break;
        // OnCalibrationChanged(false) fires just before this on X-release and hides _calibText,
        // so re-show it for the flash, then hide again (calibration has ended).
        _calibText.gameObject.SetActive(true);
        Color orig = _calibText.color;
        _calibText.text  = CoGazeStrings.Calib_Sent;
        _calibText.color = new Color(0.3f, 1f, 0.5f);
        yield return new WaitForSeconds(1.5f);
        _calibText.color = orig;
        _calibText.gameObject.SetActive(false);
    }

    public void ConnectIdentificationTask(IdentificationTask task)
    {
        if (task == null) return;
        _idTask = task;
        // Stored in a field (not an inline lambda) so OnDestroy can unsubscribe it.
        _qrHandler = qrFound =>
        {
            if (_manager == null || _manager.CurrentState != ExperimentState.TaskRunning) return;
            if (qrFound)
            {
                // Target armed (IdentificationTask fires this at task start): green instruction +
                // a light haptic tick on the answer hand as a "ready to point" cue. Use the NoGaze
                // variant in the control condition so the instruction matches the absent indicator.
                bool noGaze = _manager.CurrentGazeMode == GazeMode.None;
                SetState(noGaze ? CoGazeStrings.Worker_QRFoundNoGaze : CoGazeStrings.Worker_QRFound,
                         new Color(0.3f, 1f, 0.5f));
#if UNITY_ANDROID && !UNITY_EDITOR
                OvrHaptics.Pulse(this, 0.3f, 0.4f, 0.08f, OVRInput.Controller.RTouch);
#endif
            }
            else
                SetState(CoGazeStrings.Worker_QRSearching, new Color(0.6f, 0.9f, 1f));
        };
        task.OnQRStateChanged += _qrHandler;

        // Per-correct-hit feedback: strong haptic + green flash so the subject knows
        // their answer was taken. Fires on OnCorrectGrip (not OnTaskComplete) so it
        // triggers on EACH correct identification, not just once at end-of-task.
        _idDoneHandler = () =>
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            OvrHaptics.Pulse(this, 0.5f, 0.9f, 0.25f, OVRInput.Controller.RTouch);
#endif
            StartCoroutine(FlashIdentifyConfirm());
        };
        task.OnCorrectGrip += _idDoneHandler;

        // Update _stateText when target changes to keep QR-state message current.
        // Worker must NEVER see targetId or score.
        _idScoreHandler = (_, score) =>
        {
            if (_manager == null || _manager.CurrentState != ExperimentState.TaskRunning) return;
            if (_stateText == null) return;
            bool noGaze  = _manager.CurrentGazeMode == GazeMode.None;
            _stateText.text = noGaze ? CoGazeStrings.Worker_QRFoundNoGaze : CoGazeStrings.Worker_QRFound;
        };
        task.OnTargetChanged += _idScoreHandler;
    }

    private System.Collections.IEnumerator FlashIdentifyConfirm()
    {
        if (_stateText == null) yield break;
        Color orig = _stateText.color;
        _stateText.color = new Color(0.3f, 1f, 0.5f);
        yield return new WaitForSeconds(0.5f);
        if (_stateText != null) _stateText.color = orig;
    }

    private void HandleCountdownTick(int tick)
    {
        if (_countdownText == null) return;
        _countdownText.gameObject.SetActive(true);
        _countdownText.text = tick == 0 ? "GO！" : tick.ToString();
        if (tick == 0) StartCoroutine(HideCountdownDelayed());
    }

    private System.Collections.IEnumerator HideCountdownDelayed()
    {
        yield return new UnityEngine.WaitForSeconds(0.8f);
        if (_countdownText != null) _countdownText.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (_manager != null)
        {
            _manager.OnStateChanged       -= HandleStateChanged;
            _manager.OnInstructionChanged -= HandleInstructionChanged;
            _manager.OnProgressChanged    -= HandleProgressChanged;
            _manager.OnCountdownTick      -= HandleCountdownTick;
        }
        // MeshHandler/IdentificationTask survive a HUD teardown (e.g. reconnect), so leaving
        // these subscribed would leak and double-fire into the destroyed HUD.
        if (_meshHandler != null)
        {
            _meshHandler.OnCalibrationChanged   -= OnCalibrationChanged;
            _meshHandler.OnDualQRCalibStep      -= OnDualQRCalibStep;
            _meshHandler.OnCalibrationConfirmed -= OnCalibrationConfirmed;
            if (_outlierHandler != null)
                _meshHandler.OnDualQROutlierRejected -= _outlierHandler;
        }
        if (_idTask != null && _qrHandler      != null) _idTask.OnQRStateChanged -= _qrHandler;
        if (_idTask != null && _idDoneHandler  != null) _idTask.OnCorrectGrip   -= _idDoneHandler;
        if (_idTask != null && _idScoreHandler != null) _idTask.OnTargetChanged  -= _idScoreHandler;
        // The HUD/breath canvases are no longer children of this component's GameObject (the HUD now
        // lives in world space; the breath guide hangs off the camera rig), so destroy them
        // explicitly — otherwise a reconnect would leave a frozen, orphaned canvas in the scene.
        if (_hudCanvas != null) Destroy(_hudCanvas.gameObject);
        if (_breathGo  != null) Destroy(_breathGo);
        if (_alertMarkerGo != null) Destroy(_alertMarkerGo);
        if (_discTex != null) Destroy(_discTex);
    }

    private void Update()
    {
        if (_manager == null) return;

        RefreshConnectionStatus();
        RefreshTimer();
        RefreshAlertBillboard();
        PositionHud(instant: false);
        if (_breathingActive) AnimateBreathing();

        // Gaze availability indicator — only show when Expert is connected but Python stream is missing
        if (_gazeStatusText != null && _gazeVisualizer != null)
        {
            bool isFallback = _gazeVisualizer.IsGazeFallback;
            if (isFallback != _lastGazeAvailable)
            {
                _lastGazeAvailable = isFallback;
                _gazeStatusText.gameObject.SetActive(isFallback);
                _gazeStatusText.text  = "GAZE: FALLBACK";
                _gazeStatusText.color = new Color(1f, 0.6f, 0f);
            }
        }
    }

    // UX6: comfort-anchor the world-space HUD. Follows head position + yaw only (ignores pitch/roll)
    // with frame-rate-independent easing, so the panel stays readable above the work area instead of
    // sweeping across it when the subject looks down to assemble.
    private void PositionHud(bool instant)
    {
        if (_hudCanvas == null || _cameraAnchor == null) return;

        Vector3 fwd = Vector3.ProjectOnPlane(_cameraAnchor.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        else                          fwd.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, fwd);   // head's right, on the horizontal plane
        Vector3 target = _cameraAnchor.position
                       + fwd          * hudOffset.z
                       + right        * hudOffset.x
                       + Vector3.up   * hudOffset.y;
        Quaternion targetRot = Quaternion.LookRotation(fwd, Vector3.up);

        if (instant)
        {
            _hudCanvas.SetPositionAndRotation(target, targetRot);
            return;
        }

        float k = 1f - Mathf.Exp(-Mathf.Max(0f, hudFollowLerp) * Time.deltaTime);
        _hudCanvas.position = Vector3.Lerp(_hudCanvas.position, target, k);
        _hudCanvas.rotation = Quaternion.Slerp(_hudCanvas.rotation, targetRot, k);
    }

    // ── Setup view helpers ────────────────────────────────────────────────────

    private void ShowSetupRows(bool show)
    {
        if (_setupCalibText != null) _setupCalibText.gameObject.SetActive(show);
        if (_setupTaskText  != null) _setupTaskText.gameObject.SetActive(show);
        if (_setupHintText  != null)
        {
            // DualQRキャリブ中は _calibText に詳細指示が出るので、再表示されても二重にしない
            bool dualCalibPending = show && _meshHandler != null && _meshHandler.IsDualQRMode
                && _meshHandler.CurrentDualCalibState != DualQRCalibState.Complete;
            _setupHintText.gameObject.SetActive(show && !dualCalibPending);
        }
        if (_stateText  != null) _stateText.gameObject.SetActive(!show);
        if (_timerText  != null) _timerText.gameObject.SetActive(!show);
    }

    public void UpdateSetupStatus(bool calibDone, int taskDetected, int taskTotal, string hintText, bool expertReady)
    {
        if (_manager?.CurrentState != ExperimentState.Setup) return;

        // DualQRキャリブ中は _calibText に詳細指示が出るので _setupCalibText/_setupHintText を隠して二重表示を防ぐ
        bool dualCalibPending = _meshHandler != null && _meshHandler.IsDualQRMode && !calibDone;

        if (_connStatusText != null)
        {
            _connStatusText.text  = expertReady ? CoGazeStrings.Worker_ExpertReady : CoGazeStrings.Worker_ExpertPreparing;
            _connStatusText.color = expertReady ? new Color(0.3f, 1f, 0.5f) : new Color(1f, 0.85f, 0.3f);
        }

        if (_setupCalibText != null)
        {
            _setupCalibText.gameObject.SetActive(!dualCalibPending);
            if (!dualCalibPending)
            {
                _setupCalibText.text  = $"{(calibDone ? "[OK]" : "[--]")} キャリブレーション";
                _setupCalibText.color = calibDone ? new Color(0.3f, 1f, 0.5f) : Color.white;
            }
        }

        bool taskDone = taskDetected >= taskTotal;
        if (_setupTaskText != null)
        {
            _setupTaskText.text  = $"{(taskDone ? "[OK]" : "[--]")} タスクマーカー  {taskDetected} / {taskTotal}";
            _setupTaskText.color = taskDone ? new Color(0.3f, 1f, 0.5f) : Color.white;
        }

        if (_setupHintText != null)
        {
            _setupHintText.gameObject.SetActive(!dualCalibPending);
            if (!dualCalibPending)
            {
                _setupHintText.text  = hintText;
                _setupHintText.color = hintText.StartsWith("[OK]")
                    ? new Color(0.3f, 1f, 0.5f)
                    : new Color(1f, 0.85f, 0.3f);
            }
        }
    }

    public void ShowSetupError(string message)
    {
        if (_setupHintText == null || _manager?.CurrentState != ExperimentState.Setup) return;
        _setupHintText.text  = message;
        _setupHintText.color = new Color(1f, 0.4f, 0.3f);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void RefreshConnectionStatus()
    {
        if (_connStatusText == null) return;
        // During Setup, _connStatusText is owned by UpdateSetupStatus (fed by SetupCoordinator).
        if (_manager != null && _manager.CurrentState == ExperimentState.Setup) return;

        if (!PhotonNetwork.IsConnected)
        {
            _connStatusText.text  = CoGazeStrings.Worker_ConnDisconnected;
            _connStatusText.color = Color.red;
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
            _connStatusText.text  = CoGazeStrings.Worker_ConnExpertOnline;
            _connStatusText.color = new Color(0.3f, 1f, 0.5f);
        }
        else
        {
            _connStatusText.text  = CoGazeStrings.Worker_ConnExpertWaiting;
            _connStatusText.color = Color.yellow;
        }
    }

    private void RefreshTimer()
    {
        if (_timerText == null || _manager == null) return;

        float rem   = _manager.RemainingSeconds;
        var   state = _manager.CurrentState;

        switch (state)
        {
            case ExperimentState.WhiteNoise:
                if (_breathCountdown != null)
                    _breathCountdown.text = $"あと {FormatTime(rem)}";
                break;

            case ExperimentState.TaskRunning:
                _timerText.text  = FormatTime(rem);
                _timerText.color = rem < 5f ? Color.red : Color.white;

                if (rem <= 0f && !_taskTimerExpired)
                {
                    _taskTimerExpired = true;
                    ShowAlert();
                }
                break;

            default:
                break;
        }
    }

    private void RefreshAlertBillboard()
    {
        if (!_alertActive || _cameraAnchor == null || _alertMarkerGo == null) return;
        if (Time.time - _alertActivatedTime < 0.5f) return;

        Vector3 d = (_cameraAnchor.position - _alertMarkerGo.transform.position).normalized;
        if (d != Vector3.zero)
            _alertMarkerGo.transform.rotation = Quaternion.LookRotation(d, Vector3.up);

        Vector3 toMarker = (_alertMarkerGo.transform.position - _cameraAnchor.position).normalized;
        if (Vector3.Angle(_cameraAnchor.forward, toMarker) < lookAtAngleDeg)
            DismissAlert();
    }

    private void BuildHUD()
    {
#pragma warning disable CS0618
        var rig = Object.FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618
        _cameraAnchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
        if (_cameraAnchor == null)
        {
            Debug.LogWarning("[WorkerHUD2] No camera anchor found - HUD will not be shown.");
            return;
        }

        var go = new GameObject("WorkerHUD2_Canvas");
        go.transform.localScale = Vector3.one * hudScaleM;

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        go.GetComponent<RectTransform>().sizeDelta = hudSizeMm;

        // UX6: NOT parented to the head. The canvas lives in world space and is eased toward a
        // comfortable spot that follows head position + yaw only (see PositionHud), so head pitch
        // during assembly no longer drags the task instruction across the work area. _hudCanvas is
        // captured AFTER AddComponent<Canvas> so it references the final RectTransform.
        _hudCanvas = go.transform;
        PositionHud(instant: true);

        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(go.transform, false);
        _backgroundImage = bgGo.AddComponent<Image>();
        _backgroundImage.color = new Color(0.04f, 0.06f, 0.20f, 0.50f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        var accent    = new GameObject("Accent");
        accent.transform.SetParent(go.transform, false);
        var accentImg = accent.AddComponent<Image>();
        accentImg.color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var art = accent.GetComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = new Vector2(0f, 1f);
        art.offsetMin = Vector2.zero; art.offsetMax = new Vector2(4f, 0f);

        _connStatusText = MakeText("ConnStatus", go.transform,
            new Vector2(0.05f, 0.74f), new Vector2(0.98f, 0.98f),
            CoGazeStrings.Worker_ConnChecking, 20, TextAnchor.MiddleLeft, Color.yellow);

        _stateText = MakeText("StateText", go.transform,
            new Vector2(0.05f, 0.38f), new Vector2(0.98f, 0.76f),
            CoGazeStrings.Worker_StateIdle, 22, TextAnchor.MiddleLeft, new Color(0.6f, 0.9f, 1f));

        _timerText = MakeText("TimerText", go.transform,
            new Vector2(0.05f, 0.02f), new Vector2(0.98f, 0.40f),
            CoGazeStrings.Worker_TimerEmpty, 28, TextAnchor.MiddleCenter, Color.white);

        MakeDivider(go.transform, 0.375f);
        MakeDivider(go.transform, 0.74f);

        // Setup view: calib + task status + instructions (visible only during Setup state).
        // These overlay the _stateText/_timerText area; both sets are never shown simultaneously.
        _setupCalibText = MakeText("SetupCalibLine", go.transform,
            new Vector2(0.05f, 0.60f), new Vector2(0.98f, 0.73f),
            "[--] キャリブレーション", 20, TextAnchor.MiddleLeft, Color.white);
        _setupCalibText.gameObject.SetActive(false);

        _setupTaskText = MakeText("SetupTaskLine", go.transform,
            new Vector2(0.05f, 0.40f), new Vector2(0.98f, 0.60f),
            "[--] タスクマーカー  0 / ?", 20, TextAnchor.MiddleLeft, Color.white);
        _setupTaskText.gameObject.SetActive(false);

        _setupHintText = MakeText("SetupHintLine", go.transform,
            new Vector2(0.05f, 0.02f), new Vector2(0.98f, 0.37f),
            "QR-A と QR-B を見てください", 17, TextAnchor.UpperLeft, new Color(1f, 0.85f, 0.3f));
        _setupHintText.gameObject.SetActive(false);

        // Calibration mode indicator — hidden until grip toggles calibration ON
        _calibText = MakeText("CalibStatus", go.transform,
            new Vector2(0.0f, -0.12f), new Vector2(1.0f, 0.0f),
            "", 18, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.2f));
        _calibText.gameObject.SetActive(false);

        BuildAlertMarker();
        BuildBreathingGuide();

        // Large countdown overlay — shown once before the first condition (3-2-1-GO!)
        _countdownText = MakeText("Countdown", go.transform,
            new Vector2(0.05f, 0.20f), new Vector2(0.95f, 0.85f),
            "", 90, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.1f));
        _countdownText.gameObject.SetActive(false);

        // Gaze status indicator (bottom-right of HUD) — hidden when gaze is OK
        _gazeStatusText = MakeText("GazeStatusText", go.transform,
            new Vector2(0.5f, 0f), new Vector2(1f, 0.15f),
            "", 18, TextAnchor.LowerRight, new Color(1f, 0.6f, 0f));
        _gazeStatusText.gameObject.SetActive(false);

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
        if (_cameraAnchor == null) return;
        _alertMarkerGo = new GameObject("WorkerHUD2_AlertMarker");
        _alertMarkerGo.transform.position = _cameraAnchor.position + _cameraAnchor.forward;

        var mc = _alertMarkerGo.AddComponent<Canvas>();
        mc.renderMode = RenderMode.WorldSpace;
        _alertMarkerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(160f, 160f);
        _alertMarkerGo.transform.localScale = Vector3.one * 0.002f;

        var bgGo = new GameObject("AlertBG");
        bgGo.transform.SetParent(_alertMarkerGo.transform, false);
        bgGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.70f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        MakeText("Excl", _alertMarkerGo.transform,
            new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f),
            CoGazeStrings.Worker_AlertExclamation, 110, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.1f));

        _alertMarkerGo.SetActive(false);
    }

    private void HandleStateChanged(ExperimentState state)
    {
        // Re-enable the HUD canvas on every state transition; the Questionnaire case
        // will hide it again when needed, keeping the default "visible" contract intact.
        if (_hudCanvas != null) _hudCanvas.gameObject.SetActive(true);

        HideBreathGuide();
        ShowSetupRows(state == ExperimentState.Setup);

        if (state != ExperimentState.TaskRunning)
        {
            _taskTimerExpired = false;
            DismissAlert();
        }

        _prevState = state;

        switch (state)
        {
            case ExperimentState.Setup:
                // Setup rows managed by SetupCoordinator.UpdateSetupStatus(); just show the panel.
                SetPanelMode(true);
                break;

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
                SetTimer(FormatTime(_manager.RemainingSeconds), Color.white);
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
                // Hide the HUD entirely during the actual questionnaire to avoid canvas overlap.
                // Operator-gated sub-steps (ConditionStart, Alignment, Rest) keep the HUD visible.
                if (_manager != null && _manager.CurrentStepType == StepType.Questionnaire)
                {
                    if (_hudCanvas != null) _hudCanvas.gameObject.SetActive(false);
                }
                else
                {
                    SetState(CoGazeStrings.Worker_Questionnaire, new Color(0.4f, 0.8f, 1f));
                    SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.white);
                    SetPanelMode(true);
                }
                break;

            case ExperimentState.Tutorial:
                SetState("チュートリアル中", new Color(0.7f, 0.9f, 1f));
                SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.Finished:
                SetState(CoGazeStrings.Worker_Finished, Color.cyan);
                SetTimer(CoGazeStrings.Worker_TimerEmpty, Color.gray);
                SetPanelMode(true);
                break;

            default:
                // Unknown/added state: keep the panel visible so the HUD never silently disappears.
                Debug.LogWarning($"[WorkerHUD2] Unhandled ExperimentState in HandleStateChanged: {state}");
                SetPanelMode(true);
                break;
        }
    }

    private void HandleProgressChanged(int stepIdx, int totalSteps, StepType stepType)
    {
        var state = _manager != null ? _manager.CurrentState : ExperimentState.Idle;
        if (state == ExperimentState.TaskComplete ||
            state == ExperimentState.NoiseComplete ||
            state == ExperimentState.Finished)
            return;

        int runPos    = _manager != null ? _manager.CurrentConditionRunPosition : -1;
        int condTotal = ExperimentDesign.Conditions.Length;
        string condLabel = runPos >= 0 ? $"[条件 {runPos + 1}/{condTotal}] " : "";
        bool noGaze = _manager != null && _manager.CurrentGazeMode == GazeMode.None;

        switch (stepType)
        {
            case StepType.Noise:
                SetState(condLabel + CoGazeStrings.Worker_NoiseInProgress, Color.yellow);
                break;

            case StepType.Task:
            {
                // The authored [local] template instructed the subject to press a non-existent "Done"
                // button. The identification answer is now a proximity + grip action, so show
                // the self-contained identification instruction instead (Worker_QRFound is the whole-
                // task message per CoGazeStrings). This is also the last writer in the manager's
                // OnStateChanged→OnInstructionChanged→OnProgressChanged sequence, so it determines what
                // the subject actually sees during TaskRunning; ConnectIdentificationTask keeps it in
                // sync afterwards via OnQRStateChanged. In the NoGaze control there is no indicator, so
                // a voice-directed variant tells the subject gaze is absent this condition.
                SetState(condLabel + (noGaze ? CoGazeStrings.Worker_QRFoundNoGaze : CoGazeStrings.Worker_QRFound),
                         new Color(0.3f, 1f, 0.5f));
                break;
            }

            case StepType.Assembly:
            {
                string fileInstr    = !noGaze && _manager != null ? _manager.GetInstruction(stepIdx) : null;
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
                string fileInstrCS = _manager != null ? _manager.GetInstruction(stepIdx) : null;
                string csText = !string.IsNullOrEmpty(fileInstrCS)
                    ? fileInstrCS
                    : (runPos >= 0 ? $"条件 {runPos + 1}/{condTotal}" : CoGazeStrings.Worker_ConditionNextLabel) + CoGazeStrings.Worker_ConditionStartSuffix;
                SetState(csText, new Color(0.6f, 1f, 0.6f));
                break;
            }

            default:
                // Unknown/added StepType: leave the current HUD text untouched rather than blanking it.
                Debug.LogWarning($"[WorkerHUD2] Unhandled StepType in HandleProgressChanged: {stepType}");
                break;
        }
    }

    private void HandleInstructionChanged(string instruction)
    {
        if (!string.IsNullOrEmpty(instruction))
            SetState(instruction, new Color(0.6f, 0.9f, 1f));
    }

    private void SetPanelMode(bool full)
    {
        if (_backgroundImage != null)
            _backgroundImage.color = full
                ? new Color(0.04f, 0.06f, 0.20f, 0.50f)
                : new Color(0.02f, 0.02f, 0.08f, 0.40f);

        if (_stateText      != null) _stateText.enabled      = full;
        if (_connStatusText != null) _connStatusText.enabled  = full;
    }

    private void ShowAlert()
    {
        if (_alertMarkerGo == null || _cameraAnchor == null) return;

        Vector3 fwd   = Vector3.ProjectOnPlane(_cameraAnchor.forward, Vector3.up).normalized;
        if (fwd == Vector3.zero) fwd = Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

        Vector3 dir = (fwd + right * Mathf.Tan(40f * Mathf.Deg2Rad)).normalized;
        dir = (dir + Vector3.up * Mathf.Tan(10f * Mathf.Deg2Rad)).normalized;

        _alertMarkerGo.transform.position = _cameraAnchor.position + dir * alertDistance;
        Vector3 d = (_cameraAnchor.position - _alertMarkerGo.transform.position).normalized;
        if (d != Vector3.zero)
            _alertMarkerGo.transform.rotation = Quaternion.LookRotation(d, Vector3.up);

        _alertActive        = true;
        _alertActivatedTime = Time.time;
        _alertMarkerGo.SetActive(true);
    }

    private void DismissAlert()
    {
        if (_alertMarkerGo != null) _alertMarkerGo.SetActive(false);
        _alertActive = false;
    }

    private void SetState(string text, Color color)
    {
        if (_stateText == null) return;
        _stateText.text  = text;
        _stateText.color = color;
    }

    private void SetTimer(string text, Color color)
    {
        if (_timerText == null) return;
        _timerText.text  = text;
        _timerText.color = color;
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
        if (_cameraAnchor == null) return;

        _breathGo = new GameObject("BreathingGuide");
        _breathGo.transform.SetParent(_cameraAnchor, false);
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
        int runPos    = _manager != null ? _manager.CurrentConditionRunPosition : -1;
        int condTotal = ExperimentDesign.Conditions.Length;

        if (_breathCond != null)
            _breathCond.text = runPos >= 0
                ? $"条件  {runPos + 1} / {condTotal}  [{GazeModeLabel(_manager.CurrentGazeMode)}]"
                : CoGazeStrings.Worker_BreathIntervalLabel;

        _breathPhase    = 0f;
        _breathingActive = true;

        if (_breathGo != null) _breathGo.SetActive(true);
        if (_timerText  != null) _timerText.enabled = false;
    }

    private void HideBreathGuide()
    {
        _breathingActive = false;
        if (_breathGo  != null) _breathGo.SetActive(false);
        if (_timerText  != null) _timerText.enabled = true;
    }

    private void AnimateBreathing()
    {
        _breathPhase = (_breathPhase + Time.deltaTime / k_breathCycle) % 1f;

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
