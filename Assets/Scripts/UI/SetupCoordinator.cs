using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Tracks dual-QR calib + task-QR presence + Expert approval; drives setup UI for both roles.
public class SetupCoordinator : MonoBehaviour
{
    // ── Config ─────────────────────────────────────────────────────────────
    private bool               _isWorker;
    private int                _taskQRCount;
    private ExperimentManager2 _manager;
    private MeshHandler        _meshHandler;
    private QRSpatialManager   _qrManager;

    // ── State ──────────────────────────────────────────────────────────────
    private bool                 _calibDone;
    private readonly List<string>    _expectedTaskIds  = new();
    private readonly HashSet<string> _detectedTaskIds  = new();

    // ── Worker VR UI ───────────────────────────────────────────────────────
    // Preferred path: WorkerHUD2 found at runtime → setup status rendered inside that HUD (no
    // separate panel). Fallback path: WorkerHUD2 not found → standalone panel built by BuildWorkerUI.
    private WorkerHUD2 _workerHUD;          // non-null when merged into WorkerHUD2
    private GameObject _workerPanel;
    private Text       _workerExpertReadyLine;
    private Text       _workerCalibLine;
    private Text       _workerTaskLine;
    private Text       _workerHintLine;
    private bool       _expertSetupReady;

    // ── Manual task-QR registration feedback (UX9) ──────────────────────────
    // Declared OUTSIDE the Android #if because RefreshUI (which runs on the non-Android Expert
    // build too) reads them. They are only ever written on the Worker/Android grip path.
    private string _lastManualRegId;            // last task-QR id successfully registered, for the confirmation line
    private float  _lastManualRegTime = -10f;   // Time.time of that registration (for the brief confirmation window)

    // ── Expert UI ─────────────────────────────────────────────────────────
    private GameObject _expertPanel;
    private Text       _expertCalibLine;
    private Text       _expertTaskLine;
    private Button     _approveButton;
    private Text       _approveLabel;

    // ── Init / Destroy ─────────────────────────────────────────────────────

    public void Initialize(bool isWorker, ExperimentManager2 manager, int taskQRCount)
    {
        _isWorker    = isWorker;
        _taskQRCount = Mathf.Max(0, taskQRCount);
        _manager     = manager;
        _meshHandler = Object.FindAnyObjectByType<MeshHandler>();
        _qrManager   = Object.FindAnyObjectByType<QRSpatialManager>();

        // Expected task ids = 'A', 'B', ... ('A' + count - 1)
        for (int i = 0; i < _taskQRCount; i++)
            _expectedTaskIds.Add(((char)('A' + i)).ToString());

        if (_meshHandler != null && _meshHandler.IsDualQRMode)
        {
            // Seed from a durable flag too: if the Expert joined AFTER the Worker calibrated, the
            // buffered RPC_NotifyCalibComplete is flushed before we subscribe below, so the event
            // alone would be missed and the approve gate would deadlock. (CurrentDualCalibState is
            // never Complete on the Expert — it doesn't run the dual-QR state machine.)
            if (_meshHandler.CurrentDualCalibState == DualQRCalibState.Complete
                || _meshHandler.CalibCompleteReceived)
                _calibDone = true;
            _meshHandler.OnCalibCompleteNotified += OnCalibComplete;
        }
        else
        {
            _calibDone = true; // single-QR or no dual-QR mode: skip calib gate
        }

        if (_qrManager != null)
        {
            // Seed from already-detected markers (e.g. late joiner)
            foreach (var id in _qrManager.DetectedMarkers.Keys)
                if (IsTaskId(id)) _detectedTaskIds.Add(id);

            _qrManager.OnMarkerDetected += OnMarkerDetected;
        }

        manager.OnStateChanged += OnStateChanged;

        if (isWorker)
        {
            _workerHUD = Object.FindAnyObjectByType<WorkerHUD2>();
            if (_workerHUD == null)
                BuildWorkerUI();  // fallback: standalone panel when WorkerHUD2 is not in scene
        }
        else
            BuildExpertUI();

        RefreshUI();
        SetPanelVisible(manager.CurrentState == ExperimentState.Setup);
    }

    private void OnDestroy()
    {
        if (_meshHandler != null)
            _meshHandler.OnCalibCompleteNotified -= OnCalibComplete;
        if (_qrManager != null)
            _qrManager.OnMarkerDetected -= OnMarkerDetected;
        if (_manager != null)
            _manager.OnStateChanged -= OnStateChanged;
        if (_workerPanel != null) Destroy(_workerPanel);
        if (_expertPanel != null) Destroy(_expertPanel);
    }

    // ── Task id helpers ────────────────────────────────────────────────────

    private bool IsTaskId(string id) => _expectedTaskIds.Contains(id);

    private int DetectedTaskCount()
    {
        int n = 0;
        foreach (var id in _expectedTaskIds)
            if (_detectedTaskIds.Contains(id)) n++;
        return n;
    }

    private string FirstMissingId()
    {
        foreach (var id in _expectedTaskIds)
            if (!_detectedTaskIds.Contains(id)) return id;
        return null;
    }

    private bool AllTaskQRsPresent() => DetectedTaskCount() >= _expectedTaskIds.Count;

    // ── Event handlers ─────────────────────────────────────────────────────

    private void OnStateChanged(ExperimentState state)
    {
        SetPanelVisible(state == ExperimentState.Setup);
    }

    private void OnCalibComplete()
    {
        _calibDone = true;
        RefreshUI();
    }

    private void OnMarkerDetected(string markerId, Vector3 _, Quaternion __)
    {
        if (IsTaskId(markerId)) _detectedTaskIds.Add(markerId);
        RefreshUI();
    }

    // ── Worker manual registration (grip) ──────────────────────────────────

    private void Update()
    {
        if (_manager == null) return;

        // Expert: the approve gate also depends on the Expert's own readiness (template + OSC pong),
        // which can flip without any calib/marker event. Poll-refresh during Setup so the approve
        // button enables on time (root cause #2). Cheap (text-only) and Setup-scoped.
        if (!_isWorker)
        {
            if (_manager.CurrentState == ExperimentState.Setup) RefreshUI();
            return;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (_manager.CurrentState != ExperimentState.Setup) return;

        string nextMissing = FirstMissingId();

        float grip        = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool  gripDown    = grip > k_gripThreshold;
        bool  justPressed = gripDown && !_gripWasDown;
        _gripWasDown = gripDown;

        // While the left X button is held, the right hand is calibrating the mesh with the index
        // trigger (MeshHandler); suppress QR registration during a calibration hold so a stray grip
        // can't drop a marker while the operator is aligning the mesh.
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) return;

        if (!justPressed) return;

        // Calib QR grip fallback: when dual-QR calibration is still pending, a grip press registers
        // the controller position as the next calibration QR point (same gesture as task QR).
        if (!_calibDone && _meshHandler != null && _meshHandler.IsDualQRMode)
        {
            Vector3 calibPos = GetRightControllerWorldPos();
            if (!IsValidRegistrationPose(calibPos, out string calibRejectReason))
            {
                ShowManualRegisterRejected(_meshHandler.NextManualCalibLabel ?? "CALIB", calibRejectReason);
                OvrHaptics.Pulse(this, 0.3f, 0.3f, 0.08f, OVRInput.Controller.RTouch);
                return;
            }
            bool accepted = _meshHandler.TryManualCalibRegister(calibPos);
            if (accepted)
                OvrHaptics.Pulse(this, 0.5f, 0.8f, 0.2f, OVRInput.Controller.RTouch);
            return;
        }

        if (nextMissing == null || _qrManager == null) return;

        Vector3    pos = GetRightControllerWorldPos();
        Quaternion rot = _ovrRig != null ? _ovrRig.rightHandAnchor.rotation : Quaternion.identity;

        // UX9: a manual registration writes this raw controller position straight into the marker
        // set that the LATER 20 cm identification proximity test compares against
        // (IdentificationTask.ProximityThreshold = 0.20 m). A garbage pose — tracking lost (origin),
        // NaN, or an arm's-reach-violating stray grip — would silently corrupt that test with no
        // operator-visible symptom. Gate the registration on a sane pose and tell the operator to
        // retry instead of recording garbage.
        if (!IsValidRegistrationPose(pos, out string rejectReason))
        {
            Debug.LogWarning($"[SetupCoordinator] Manual register REJECTED for '{nextMissing}': {rejectReason} pos={pos}");
            ShowManualRegisterRejected(nextMissing, rejectReason);
            OvrHaptics.Pulse(this, 0.3f, 0.3f, 0.08f, OVRInput.Controller.RTouch); // soft "rejected" buzz
            return;
        }

        // Set the confirmation BEFORE registering: RegisterManualMarker broadcasts via a PUN RPC
        // that runs locally and SYNCHRONOUSLY, so the RefreshUI it triggers (via OnMarkerDetected)
        // must already see this id, or the on-panel "registered" line shows the previous letter.
        _lastManualRegId   = nextMissing;
        _lastManualRegTime = Time.time;
        _qrManager.RegisterManualMarker(nextMissing, pos, rot);

        OvrHaptics.Pulse(this, 0.5f, 0.8f, 0.2f, OVRInput.Controller.RTouch);
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private const float k_gripThreshold    = OVRInputThresholds.Grip;
    private const float k_maxRegisterReach = 1.2f;  // arm's reach plausibility guard (NOT the 20 cm identification threshold)
    private bool         _gripWasDown;
    private OVRCameraRig _ovrRig;

    private bool IsValidRegistrationPose(Vector3 pos, out string reason)
    {
        if (float.IsNaN(pos.x)      || float.IsNaN(pos.y)      || float.IsNaN(pos.z) ||
            float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z))
        {
            reason = "コントローラ座標が無効です";
            return false;
        }

        // A controller that has lost tracking reports (0,0,0) at the tracking origin.
        if (pos.sqrMagnitude < 0.0001f)
        {
            reason = "コントローラが認識されていません";
            return false;
        }

        Transform head = _ovrRig != null ? _ovrRig.centerEyeAnchor : null;
        if (head != null && Vector3.Distance(pos, head.position) > k_maxRegisterReach)
        {
            reason = "QRにもっと近づけてください";
            return false;
        }

        reason = null;
        return true;
    }

    private void ShowManualRegisterRejected(string id, string reason)
    {
        string msg = $"[再試行] 「{id}」を登録できません\n{reason}";
        if (_workerHUD != null) { _workerHUD.ShowSetupError(msg); return; }
        if (_workerHintLine == null) return;
        _workerHintLine.text  = msg;
        _workerHintLine.color = new Color(1f, 0.4f, 0.3f);
    }

    private Vector3 GetRightControllerWorldPos()
    {
        if (_ovrRig == null) _ovrRig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (_ovrRig != null) return _ovrRig.rightHandAnchor.position;
        return OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
    }
#endif

    // ── UI ──────────────────────────────────────────────────────────────────

    private void SetPanelVisible(bool visible)
    {
        if (_workerPanel != null) _workerPanel.SetActive(visible);
        if (_expertPanel != null) _expertPanel.SetActive(visible);
    }

    private void RefreshUI()
    {
        // CQ19: plain-ASCII status markers — the bundled NotoSansJP font has no emoji glyphs,
        // so ✅/⬜ rendered as tofu (□) on the headset.
        string calibIcon   = _calibDone ? "[OK]" : "[--]";
        bool   taskDone    = AllTaskQRsPresent();
        string taskIcon    = taskDone ? "[OK]" : "[--]";
        int    detected    = DetectedTaskCount();
        string firstMissing = FirstMissingId();

        if (_isWorker)
        {
            // Build hint text (shared by merged and standalone paths)
            bool allDone = _calibDone && taskDone;
            string hintText;
            if (allDone)
            {
                hintText = "[OK] 準備完了 — 実験者の承認を待っています";
            }
            else if (!_calibDone)
            {
                string cA = _meshHandler?.CalibQRColorA ?? "赤色の枠";
                string cB = _meshHandler?.CalibQRColorB ?? "青色の枠";
                bool needsA = _meshHandler == null
                    || _meshHandler.CurrentDualCalibState == DualQRCalibState.NeedsA;
                hintText = needsA
                    ? CoGazeStrings.DualCalib_NeedsA(cA)
                    : CoGazeStrings.DualCalib_NeedsB(cA, cB);
            }
            else
            {
                bool justRegistered = !string.IsNullOrEmpty(_lastManualRegId)
                                      && (Time.time - _lastManualRegTime) < 3f;
                string confirm = justRegistered
                    ? $"[OK] 「{_lastManualRegId}」を登録しました\n"
                    : "";
                hintText = confirm +
                    $"未認識のQRが {_expectedTaskIds.Count - detected} 個あります\n" +
                    $"「{firstMissing}」のQRにコントローラを当てて\n右グリップを押してください";
            }

            if (_workerHUD != null)
            {
                _workerHUD.UpdateSetupStatus(_calibDone, detected, _expectedTaskIds.Count, hintText, _expertSetupReady);
                return;
            }

            // Standalone fallback panel
            if (_workerCalibLine != null)
                _workerCalibLine.text = $"{calibIcon} キャリブレーション";
            if (_workerTaskLine != null)
                _workerTaskLine.text = $"{taskIcon} タスクマーカー  {detected} / {_expectedTaskIds.Count}";
            if (_workerHintLine != null)
            {
                _workerHintLine.text  = hintText;
                _workerHintLine.color = allDone
                    ? new Color(0.3f, 1f, 0.5f)
                    : !_calibDone
                        ? new Color(1f, 0.85f, 0.3f)
                        : (!string.IsNullOrEmpty(_lastManualRegId) && (Time.time - _lastManualRegTime) < 3f
                            ? new Color(0.4f, 1f, 0.5f)
                            : new Color(1f, 0.6f, 0.3f));
            }
        }
        else
        {
            if (_expertCalibLine != null)
                _expertCalibLine.text = $"{calibIcon} キャリブ";

            if (_expertTaskLine != null)
            {
                string miss = taskDone ? "" : $"  (未: {firstMissing}…)";
                _expertTaskLine.text = $"{taskIcon} タスクQR  {detected} / {_expectedTaskIds.Count}{miss}";
            }

            // Gate also on the Expert's OWN readiness (template + OSC pong) so the operator can't
            // approve before their side is ready. Read locally from the manager — no network. If
            // the manager is somehow absent, fall back to the prior calib+task gate (never deadlock).
            bool selfReady  = _manager == null || _manager.IsExpertSelfReady;
            bool canApprove = _calibDone && taskDone && selfReady;
            if (_approveButton != null)
            {
                _approveButton.interactable = canApprove;
                var bg = _approveButton.GetComponent<Image>();
                if (bg != null)
                    bg.color = canApprove
                        ? new Color(0.10f, 0.55f, 0.25f)
                        : new Color(0.20f, 0.22f, 0.22f);
            }
            if (_approveLabel != null)
            {
                // Surface WHY approval is blocked instead of an inert grey button.
                if (canApprove)                    _approveLabel.text = "承認して実験開始";
                else if (!_calibDone || !taskDone) _approveLabel.text = "Worker のセットアップ待ち";
                else                               _approveLabel.text = "自分の準備中…(テンプレ/OSC)";
                _approveLabel.color = canApprove ? Color.white : new Color(0.6f, 0.6f, 0.6f);
            }
        }
    }

    // ── Build Worker UI ────────────────────────────────────────────────────

    private void BuildWorkerUI()
    {
#pragma warning disable CS0618
        var rig = Object.FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618
        Transform anchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
        if (anchor == null) return;

        _workerPanel = new GameObject("SetupCoordinator_WorkerPanel");
        _workerPanel.transform.SetParent(anchor, false);
        _workerPanel.transform.localPosition = new Vector3(0f, 0.18f, 1.0f);
        _workerPanel.transform.localRotation = Quaternion.identity;
        _workerPanel.transform.localScale    = Vector3.one * 0.001f;

        var canvas = _workerPanel.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _workerPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(480f, 200f);

        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(_workerPanel.transform, false);
        bgGo.AddComponent<Image>().color = new Color(0.04f, 0.06f, 0.20f, 0.88f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        var accent = new GameObject("Accent");
        accent.transform.SetParent(_workerPanel.transform, false);
        accent.AddComponent<Image>().color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var art = accent.GetComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = new Vector2(0f, 1f);
        art.offsetMin = Vector2.zero; art.offsetMax = new Vector2(4f, 0f);

        MakeText("Header", _workerPanel.transform,
            new Vector2(0.04f, 0.83f), new Vector2(0.96f, 0.99f),
            "セットアップ中", 22, TextAnchor.MiddleLeft, new Color(0.5f, 0.8f, 1f));

        // Expert setup-readiness — fed from SceneBootstrapper2 via the "expertSetupReady" Photon prop.
        _workerExpertReadyLine = MakeText("ExpertReadyLine", _workerPanel.transform,
            new Vector2(0.04f, 0.67f), new Vector2(0.96f, 0.83f),
            CoGazeStrings.Worker_ExpertPreparing, 18, TextAnchor.MiddleLeft, new Color(1f, 0.85f, 0.3f));

        _workerCalibLine = MakeText("CalibLine", _workerPanel.transform,
            new Vector2(0.04f, 0.51f), new Vector2(0.96f, 0.67f),
            "[--] キャリブレーション", 20, TextAnchor.MiddleLeft, Color.white);

        _workerTaskLine = MakeText("TaskLine", _workerPanel.transform,
            new Vector2(0.04f, 0.35f), new Vector2(0.96f, 0.51f),
            "[--] タスクマーカー  0 / ?", 20, TextAnchor.MiddleLeft, Color.white);

        _workerHintLine = MakeText("HintLine", _workerPanel.transform,
            new Vector2(0.04f, 0.02f), new Vector2(0.96f, 0.35f),
            "QR-A と QR-B を見てください", 17, TextAnchor.UpperLeft, new Color(1f, 0.85f, 0.3f));

        RefreshExpertReadyLine();
    }

    // ── Expert setup-readiness (Worker display) ─────────────────────────────

    public void SetExpertSetupReady(bool ready)
    {
        _expertSetupReady = ready;
        if (_workerHUD != null)
            RefreshUI();  // WorkerHUD2 path — UpdateSetupStatus includes the expert-ready line
        else
            RefreshExpertReadyLine();
    }

    private void RefreshExpertReadyLine()
    {
        if (_workerExpertReadyLine == null) return;
        _workerExpertReadyLine.text  = _expertSetupReady
            ? CoGazeStrings.Worker_ExpertReady
            : CoGazeStrings.Worker_ExpertPreparing;
        _workerExpertReadyLine.color = _expertSetupReady
            ? new Color(0.3f, 1f, 0.5f)
            : new Color(1f, 0.85f, 0.3f);
    }

    // ── Build Expert UI ────────────────────────────────────────────────────

    private void BuildExpertUI()
    {
        var canvasGo = new GameObject("SetupCoordinator_ExpertPanel");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        canvasGo.AddComponent<GraphicRaycaster>();
        _expertPanel = canvasGo;

        // Ensure EventSystem exists for button clicks
        if (Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
#if ENABLE_INPUT_SYSTEM
            esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
        }

        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        panelGo.AddComponent<Image>().color = new Color(0.04f, 0.06f, 0.20f, 0.92f);
        var rt = panelGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.68f, 0.48f);
        rt.anchorMax = new Vector2(0.97f, 0.92f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var pt = panelGo.transform;

        MakeText("Header", pt,
            new Vector2(0.05f, 0.82f), new Vector2(0.95f, 1.00f),
            "Worker セットアップ", 19, TextAnchor.MiddleCenter, new Color(0.5f, 0.8f, 1f));

        _expertCalibLine = MakeText("CalibLine", pt,
            new Vector2(0.05f, 0.62f), new Vector2(0.95f, 0.81f),
            "[--] キャリブ", 17, TextAnchor.MiddleLeft, Color.white);

        _expertTaskLine = MakeText("TaskLine", pt,
            new Vector2(0.05f, 0.43f), new Vector2(0.95f, 0.62f),
            "[--] タスクQR  0 / ?", 17, TextAnchor.MiddleLeft, Color.white);

        var btnGo = new GameObject("ApproveButton");
        btnGo.transform.SetParent(pt, false);
        var btnImg = btnGo.AddComponent<Image>();
        btnImg.color = new Color(0.20f, 0.22f, 0.22f);
        _approveButton = btnGo.AddComponent<Button>();
        _approveButton.targetGraphic = btnImg;
        _approveButton.interactable  = false;
        var btnRt = btnGo.GetComponent<RectTransform>();
        btnRt.anchorMin = new Vector2(0.05f, 0.05f);
        btnRt.anchorMax = new Vector2(0.95f, 0.32f);
        btnRt.offsetMin = btnRt.offsetMax = Vector2.zero;

        _approveLabel = MakeText("BtnLabel", btnGo.transform,
            Vector2.zero, Vector2.one,
            "承認して実験開始", 17, TextAnchor.MiddleCenter, new Color(0.5f, 0.5f, 0.5f));

        _approveButton.onClick.AddListener(() =>
        {
            if (_manager != null) _manager.TriggerSetupComplete();
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void StretchFill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private Text MakeText(string name, Transform parent,
                          Vector2 anchorMin, Vector2 anchorMax,
                          string text, int size,
                          TextAnchor alignment, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.text               = text;
        t.fontSize           = size;
        t.alignment          = alignment;
        t.color              = color;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow   = VerticalWrapMode.Overflow;

        var f = Resources.Load<Font>("Fonts/NotoSansJP-Regular");
        if (f == null) f = Resources.Load<Font>("Fonts/NotoSansCJK-Regular");
        if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f != null) t.font = f;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return t;
    }
}
