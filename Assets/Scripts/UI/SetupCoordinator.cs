using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tracks the setup conditions (dual-QR calib + all task QRs present + Expert approval) and
/// drives setup UI for Worker (VR WorldSpace) and Expert (ScreenSpace).
///
/// Task QRs are expected to carry single-letter ids 'A'..'A'+(requiredTaskQRCount-1). Because
/// Quest's MRUK can only auto-track ~6 QR codes at once, any task QR that never auto-detects is
/// recovered manually: the Worker is shown which letter to register, touches that physical QR
/// with the right controller, and presses grip — SetupCoordinator registers the controller's
/// position as that id via QRSpatialManager.RegisterManualMarker (no MRUK slot consumed).
/// </summary>
public class SetupCoordinator : MonoBehaviour
{
    // ── Config ─────────────────────────────────────────────────────────────
    private bool               _isWorker;
    private int                _taskQRCount;
    private ExperimentManager2 _manager;
    private MeshHandler        _meshHandler;
    private QRSpatialManager   _qrManager;

    // ── State ──────────────────────────────────────────────────────────────
    private bool                 _calibDone       = false;
    private readonly List<string>    _expectedTaskIds  = new();
    private readonly HashSet<string> _detectedTaskIds  = new();

#if UNITY_ANDROID && !UNITY_EDITOR
    private const float GripThreshold = 0.7f;
    private bool         _gripWasDown = false;
    private OVRCameraRig _ovrRig;
#endif

    // ── Worker VR UI ───────────────────────────────────────────────────────
    private GameObject _workerPanel;
    private Text       _workerCalibLine;
    private Text       _workerTaskLine;
    private Text       _workerHintLine;

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
            BuildWorkerUI();
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

    /// <summary>A task QR id is any detected marker that is in the expected set (excludes calib).</summary>
    private bool IsTaskId(string id) => _expectedTaskIds.Contains(id);

    private int DetectedTaskCount()
    {
        int n = 0;
        foreach (var id in _expectedTaskIds)
            if (_detectedTaskIds.Contains(id)) n++;
        return n;
    }

    /// <summary>First expected task id not yet detected/registered, or null if all present.</summary>
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
        bool  gripDown    = grip > GripThreshold;
        bool  justPressed = gripDown && !_gripWasDown;
        _gripWasDown = gripDown;

        // While the left X button is held, the right grip is calibrating the mesh (MeshHandler) —
        // don't also register a QR with the same grip press.
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) return;

        if (!justPressed || nextMissing == null || _qrManager == null) return;

        Vector3    pos = GetRightControllerWorldPos();
        Quaternion rot = _ovrRig != null ? _ovrRig.rightHandAnchor.rotation : Quaternion.identity;
        _qrManager.RegisterManualMarker(nextMissing, pos, rot);

        OVRInput.SetControllerVibration(0.5f, 0.8f, OVRInput.Controller.RTouch);
        StartCoroutine(StopVibration(0.2f));
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private Vector3 GetRightControllerWorldPos()
    {
        if (_ovrRig == null) _ovrRig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (_ovrRig != null) return _ovrRig.rightHandAnchor.position;
        return OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
    }

    private IEnumerator StopVibration(float delay)
    {
        yield return new WaitForSeconds(delay);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
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
        string calibIcon   = _calibDone ? "✅" : "⬜";
        bool   taskDone    = AllTaskQRsPresent();
        string taskIcon    = taskDone ? "✅" : "⬜";
        int    detected    = DetectedTaskCount();
        string firstMissing = FirstMissingId();

        if (_isWorker)
        {
            if (_workerCalibLine != null)
                _workerCalibLine.text = $"{calibIcon} キャリブレーション";

            if (_workerTaskLine != null)
                _workerTaskLine.text = $"{taskIcon} タスクマーカー  {detected} / {_expectedTaskIds.Count}";

            bool allDone = _calibDone && taskDone;
            if (_workerHintLine != null)
            {
                if (allDone)
                {
                    _workerHintLine.text  = "✅ 準備完了 — 実験者の承認を待っています";
                    _workerHintLine.color = new Color(0.3f, 1f, 0.5f);
                }
                else if (!_calibDone)
                {
                    _workerHintLine.text  = "QR-A と QR-B を見てください";
                    _workerHintLine.color = new Color(1f, 0.85f, 0.3f);
                }
                else // calib done, task QRs missing
                {
                    _workerHintLine.text  =
                        $"未認識のQRが {_expectedTaskIds.Count - detected} 個あります\n" +
                        $"「{firstMissing}」のQRにコントローラを当てて\n右グリップを押してください";
                    _workerHintLine.color = new Color(1f, 0.6f, 0.3f);
                }
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
            new Vector2(0.04f, 0.80f), new Vector2(0.96f, 0.98f),
            "セットアップ中", 22, TextAnchor.MiddleLeft, new Color(0.5f, 0.8f, 1f));

        _workerCalibLine = MakeText("CalibLine", _workerPanel.transform,
            new Vector2(0.04f, 0.62f), new Vector2(0.96f, 0.80f),
            "⬜ キャリブレーション", 20, TextAnchor.MiddleLeft, Color.white);

        _workerTaskLine = MakeText("TaskLine", _workerPanel.transform,
            new Vector2(0.04f, 0.44f), new Vector2(0.96f, 0.62f),
            "⬜ タスクマーカー  0 / ?", 20, TextAnchor.MiddleLeft, Color.white);

        _workerHintLine = MakeText("HintLine", _workerPanel.transform,
            new Vector2(0.04f, 0.02f), new Vector2(0.96f, 0.44f),
            "QR-A と QR-B を見てください", 17, TextAnchor.UpperLeft, new Color(1f, 0.85f, 0.3f));
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
        rt.anchorMin = new Vector2(0.73f, 0.55f);
        rt.anchorMax = new Vector2(0.99f, 0.93f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var pt = panelGo.transform;

        MakeText("Header", pt,
            new Vector2(0.05f, 0.82f), new Vector2(0.95f, 1.00f),
            "Worker セットアップ", 19, TextAnchor.MiddleCenter, new Color(0.5f, 0.8f, 1f));

        _expertCalibLine = MakeText("CalibLine", pt,
            new Vector2(0.05f, 0.62f), new Vector2(0.95f, 0.81f),
            "⬜ キャリブ", 17, TextAnchor.MiddleLeft, Color.white);

        _expertTaskLine = MakeText("TaskLine", pt,
            new Vector2(0.05f, 0.43f), new Vector2(0.95f, 0.62f),
            "⬜ タスクQR  0 / ?", 17, TextAnchor.MiddleLeft, Color.white);

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
