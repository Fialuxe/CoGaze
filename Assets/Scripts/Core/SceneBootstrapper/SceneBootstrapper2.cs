using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Management;
using Photon.Pun;
using Photon.Realtime;
using Photon.Voice;
using Photon.Voice.Unity;
using Photon.Voice.PUN;
using POpusCodec.Enums;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

/// <summary>
/// Entry point for ExperimentScene (10-condition design).
/// Mirrors SceneBootstrapper but uses ExperimentManager2, WorkerHUD2, ExpertUI2.
///
/// Pre-place in scene:
///   [Bootstrapper] with this script
///   OVRCameraRigSetup (building blocks prefab)
///   [Managers]/ExperimentManager  → ExperimentManager2 + AudioSource
///   [Managers]/QRSpatialManager   → QRSpatialManager + PhotonView
///   [Managers]/QuestionnaireManager → QuestionnaireManager + PhotonView
///   [Tasks]/IdentificationTask    → IdentificationTask + PhotonView
///   [Tasks]/AssemblyTask          → AssemblyTask (grid positioned via QR)
/// </summary>
public class SceneBootstrapper2 : MonoBehaviourPunCallbacks, IOnEventCallback
{
    [Header("Participant")]
    [Tooltip("Williams table row (0-9).")]
    public int    participantOrderIndex = 0;
    public string participantId        = "P00";

#if UNITY_EDITOR
    [Header("Editor Debug")]
    [Tooltip("Force Worker role in Play Mode for testing without Android build.")]
    [SerializeField] private bool _editorForceWorkerRole = false;
#endif

    private const string WORKER_PREFAB = "Prefabs/LocalWorker";
    private const string EXPERT_PREFAB = "Prefabs/RemoteExpert";

    private NetworkManager     networkManager;
    private string             _role;
    private bool               _setupDone        = false;
    private bool               _offlineMode      = false;
    private string             _selectedMic      = "";
    private WorkerVideoStream  _videoStream;      // Worker side — for WebRTC signaling
    private ExpertVideoDisplay _videoDisplay;     // Expert side — for WebRTC signaling
    private VoiceRecorder      _voiceRecorder;
    private bool               _offerTriggered;
    private bool               _expertAudioAttached;

    // ── Awake ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        // OVRCameraRig already provides an AudioListener; remove extras to prevent
        // "2 audio listeners" console spam every frame at startup.
        var allListeners = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
        for (int i = 1; i < allListeners.Length; i++)
            DestroyImmediate(allListeners[i]);

        string logBase = Application.platform == RuntimePlatform.Android
            ? Application.persistentDataPath
            : System.IO.Path.Combine(Application.dataPath, "..");
        string logPath = System.IO.Path.Combine(logBase, $"cogaze_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        FileLogger.Init(logPath);
        gameObject.AddComponent<UnityLogCapture>();
        FileLogger.Log("Setup", $"[FileLogger] {logPath}");
        var nmObj = new GameObject("NetworkManager");
        networkManager = nmObj.AddComponent<NetworkManager>();
        networkManager.OnRoomJoined          += OnRoomJoined;
        networkManager.OnNetworkDisconnected += OnPhotonDisconnected;
        StartCoroutine(StartupFlow());
        FileLogger.Log("Setup", "[SceneBootstrapper2] Starting startup flow...");
    }

    // ── StartupFlow ────────────────────────────────────────────────────────

    private IEnumerator StartupFlow()
    {
        var config = StartupConfig.LoadOrDefault();

        // Android (Quest) boots headless — no keyboard/monitor to interact with the config panel.
        // The config is pre-written to disk via StartupConfig.Save() on the PC side first.
        if (Application.platform != RuntimePlatform.Android)
        {
            bool confirmed = false;
            var ui = gameObject.AddComponent<StartupUI>();
            ui.Initialize(config);
            ui.OnConfirmed += () => confirmed = true;
            yield return new WaitUntil(() => confirmed);
        }

        // Apply config
        participantId         = config.participantId;
        participantOrderIndex = config.participantOrderIndex;
        _selectedMic          = config.microphoneDevice;
        _offlineMode          = config.offlineMode;
        config.Save();

        var oscMgr = FindAnyObjectByType<OscSessionManager>();
        oscMgr?.SetPythonHost(config.pythonHost);

        if (_offlineMode)
        {
            // Skip Photon — run setup immediately with locally-detected role
            DetectRole();
            RoleManager.SetRole(_role);
            ConfigureXR(_role);
            StartCoroutine(SetupAfterDeviceCheck());
        }
        else
        {
            networkManager.Connect();
        }
    }

    // ── ShowStartupPanel ───────────────────────────────────────────────────

    private void ShowStartupPanel(StartupConfig config, System.Action onConfirm)
    {
        // Create Screen Space Overlay Canvas
        var canvasObj = new GameObject("StartupConfigCanvas");
        var canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        // Background panel
        var bgObj = new GameObject("Background");
        bgObj.transform.SetParent(canvasObj.transform, false);
        var bgImage = bgObj.AddComponent<Image>();
        bgImage.color = new Color(0f, 0f, 0f, 0.75f);
        var bgRect = bgObj.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;

        // Centered panel
        var panelObj = new GameObject("Panel");
        panelObj.transform.SetParent(canvasObj.transform, false);
        var panelImage = panelObj.AddComponent<Image>();
        panelImage.color = new Color(0.15f, 0.15f, 0.15f, 0.95f);
        var panelRect = panelObj.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(800f, 400f);
        panelRect.anchoredPosition = Vector2.zero;

        // Title text
        var titleObj = new GameObject("Title");
        titleObj.transform.SetParent(panelObj.transform, false);
        var titleText = titleObj.AddComponent<Text>();
        titleText.text = CoGazeStrings.Boot_Title;
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        titleText.fontSize = 28;
        titleText.fontStyle = FontStyle.Bold;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        var titleRect = titleObj.GetComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.offsetMin = new Vector2(0f, -60f);
        titleRect.offsetMax = new Vector2(0f, 0f);

        // ParticipantConfigUI sub-panel
        var participantPanelObj = new GameObject("ParticipantConfigPanel");
        participantPanelObj.transform.SetParent(panelObj.transform, false);
        participantPanelObj.AddComponent<Image>().color = Color.clear;
        var participantRect = participantPanelObj.GetComponent<RectTransform>();
        participantRect.anchorMin = new Vector2(0f, 0.5f);
        participantRect.anchorMax = new Vector2(0.5f, 1f);
        participantRect.offsetMin = new Vector2(10f, -180f);
        participantRect.offsetMax = new Vector2(-5f, -60f);
        var participantUI = participantPanelObj.AddComponent<ParticipantConfigUI>();
        participantUI.Initialize(config);

        // ConnectionConfigUI sub-panel
        var connectionPanelObj = new GameObject("ConnectionConfigPanel");
        connectionPanelObj.transform.SetParent(panelObj.transform, false);
        connectionPanelObj.AddComponent<Image>().color = Color.clear;
        var connectionRect = connectionPanelObj.GetComponent<RectTransform>();
        connectionRect.anchorMin = new Vector2(0.5f, 0.5f);
        connectionRect.anchorMax = new Vector2(1f, 1f);
        connectionRect.offsetMin = new Vector2(5f, -180f);
        connectionRect.offsetMax = new Vector2(-10f, -60f);
        var connectionUI = connectionPanelObj.AddComponent<ConnectionConfigUI>();
        connectionUI.Initialize(config);

        // Start button
        var buttonObj = new GameObject("StartButton");
        buttonObj.transform.SetParent(panelObj.transform, false);
        var button = buttonObj.AddComponent<Button>();
        var buttonImage = buttonObj.AddComponent<Image>();
        buttonImage.color = new Color(0.2f, 0.6f, 1f, 1f);
        button.targetGraphic = buttonImage;
        var buttonRect = buttonObj.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0f);
        buttonRect.anchorMax = new Vector2(0.5f, 0f);
        buttonRect.pivot = new Vector2(0.5f, 0f);
        buttonRect.sizeDelta = new Vector2(200f, 50f);
        buttonRect.anchoredPosition = new Vector2(0f, 20f);

        var buttonTextObj = new GameObject("Text");
        buttonTextObj.transform.SetParent(buttonObj.transform, false);
        var buttonText = buttonTextObj.AddComponent<Text>();
        buttonText.text = CoGazeStrings.Boot_Start;
        buttonText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        buttonText.fontSize = 22;
        buttonText.fontStyle = FontStyle.Bold;
        buttonText.alignment = TextAnchor.MiddleCenter;
        buttonText.color = Color.white;
        var buttonTextRect = buttonTextObj.GetComponent<RectTransform>();
        buttonTextRect.anchorMin = Vector2.zero;
        buttonTextRect.anchorMax = Vector2.one;
        buttonTextRect.offsetMin = Vector2.zero;
        buttonTextRect.offsetMax = Vector2.zero;

        button.onClick.AddListener(() =>
        {
            participantUI.Apply(config);
            connectionUI.Apply(config);
            Destroy(canvasObj);
            onConfirm();
        });
    }

    // ── OnRoomJoined ──────────────────────────────────────────────────────

    private void OnRoomJoined()
    {
        DetectRole();
        RoleManager.SetRole(_role);
        ConfigureXR(_role);

        // Connect PunVoiceClient after PUN room join to avoid "Provide an AppId" error
        // that occurs when AutoConnectAndJoin fires before Photon Realtime is connected.
        var pvc = FindAnyObjectByType<PunVoiceClient>();
        if (pvc != null && !pvc.Client.IsConnected)
        {
            FileLogger.Log("Setup", "[SceneBootstrapper2] Connecting PunVoiceClient after room join.");
            pvc.ConnectAndJoinRoom();
        }

        StartCoroutine(SetupAfterDeviceCheck());
    }

    // ── Role detection ────────────────────────────────────────────────────

    private void DetectRole()
    {
#if UNITY_EDITOR
        // In the Editor there is no physical Android device, so platform-based detection always
        // returns Expert.  RoleBasedBootSystem (or the force-Worker toggle) lets a single PC
        // simulate the Worker path without deploying to the Quest.
        if (_editorForceWorkerRole) { _role = RoleManager.ROLE_WORKER; FileLogger.Log("Setup", "[SceneBootstrapper2] EDITOR: forced Worker role"); return; }

        var bootSystem = GetComponent<RoleBasedBootSystem>() ?? FindAnyObjectByType<RoleBasedBootSystem>();
        if (bootSystem != null)
        {
            _role = bootSystem.SelectedRole == AppRole.Expert ? RoleManager.ROLE_EXPERT : RoleManager.ROLE_WORKER;
            FileLogger.Log("Setup", $"[SceneBootstrapper2] Role from RoleBasedBootSystem: {_role}");
            return;
        }
#endif
        _role = Application.platform == RuntimePlatform.Android
            ? RoleManager.ROLE_WORKER
            : RoleManager.ROLE_EXPERT;
        FileLogger.Log("Setup", $"[SceneBootstrapper2] Role={_role} platform={Application.platform}");
    }

    // ── XR ────────────────────────────────────────────────────────────────

    private void ConfigureXR(string role)
    {
        var xrSettings = XRGeneralSettings.Instance;
        if (xrSettings == null || xrSettings.Manager == null) return;

        if (role == RoleManager.ROLE_EXPERT)
        {
            if (xrSettings.Manager.isInitializationComplete)
            {
                xrSettings.Manager.StopSubsystems();
#if !UNITY_EDITOR
                // DeinitializeLoader() is needed in builds to fully stop the XR plugin.
                // In the Editor it blocks indefinitely waiting for the OVR compositor
                // to tear down (confirmed: log stops at CompositorOpenXR::~CompositorOpenXR).
                // The Editor manages XR lifecycle via Play mode — skip here.
                xrSettings.Manager.DeinitializeLoader();
#endif
            }
        }
        else
        {
            if (!xrSettings.Manager.isInitializationComplete)
            {
                xrSettings.Manager.InitializeLoaderSync();
                xrSettings.Manager.StartSubsystems();
            }
        }
    }

    // ── Main setup ────────────────────────────────────────────────────────

    private IEnumerator SetupAfterDeviceCheck()
    {
        string mic = !string.IsNullOrEmpty(_selectedMic)
            ? _selectedMic
            : (Microphone.devices.Length > 0 ? Microphone.devices[0] : "");
        FileLogger.Log("Setup", $"[SceneBootstrapper2] Mic: '{(mic == "" ? "(default)" : mic)}'");

        bool isExpert = _role == RoleManager.ROLE_EXPERT;

        var expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (expMgr == null)
        {
            Debug.LogError("[SceneBootstrapper2] ExperimentManager2 not found in scene.");
            yield break;
        }
        expMgr.participantOrderIndex = participantOrderIndex;
        expMgr.participantId         = participantId;

        GameObject playerObj;
        if (_offlineMode)
        {
            // Offline: instantiate locally, skip Photon ownership check
            var prefab = Resources.Load<GameObject>(isExpert ? EXPERT_PREFAB : WORKER_PREFAB);
            if (prefab == null)
            {
                Debug.LogError($"[SceneBootstrapper2] Prefab not found: {(isExpert ? EXPERT_PREFAB : WORKER_PREFAB)}");
                yield break;
            }
            playerObj = Instantiate(prefab, Vector3.zero, Quaternion.identity);
        }
        else
        {
            playerObj = PhotonNetwork.Instantiate(
                isExpert ? EXPERT_PREFAB : WORKER_PREFAB, Vector3.zero, Quaternion.identity);
            if (playerObj == null)
            {
                Debug.LogError($"[SceneBootstrapper2] PhotonNetwork.Instantiate returned null for {(isExpert ? EXPERT_PREFAB : WORKER_PREFAB)}");
                yield break;
            }
            var view = playerObj.GetComponent<PhotonView>();
            if (view == null)
            {
                Debug.LogError("[SceneBootstrapper2] PhotonView missing on instantiated prefab.");
                yield break;
            }
            if (!view.IsMine)
            {
                expMgr.Initialize(isExpert: isExpert);
                _setupDone = true;
                yield break;
            }
        }

        if (!isExpert)
            SetupWorker(playerObj, expMgr, mic);
        else
            SetupExpert(playerObj, expMgr, mic);

        _setupDone = true;
        FileLogger.Log("Setup", $"[SceneBootstrapper2] Setup complete. Role={_role}");
    }

    // ── Worker ────────────────────────────────────────────────────────────

    private void SetupWorker(GameObject playerObj, ExperimentManager2 expMgr, string micDevice)
    {
        // Disable default camera; ensure AudioListener remains in scene
        var cam = Camera.main;
        if (cam != null && cam.GetComponentInParent<OVRCameraRig>() == null)
        {
            // Check if any AudioListener exists OUTSIDE the camera hierarchy.
            // If all listeners are on the camera (or its children), create a standalone one
            // before disabling the camera — otherwise audio goes silent.
            bool hasExternalAL = false;
            foreach (var al in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (!al.transform.IsChildOf(cam.transform))
                { hasExternalAL = true; break; }
            }
            if (!hasExternalAL)
                new GameObject("WorkerAudioListener").AddComponent<AudioListener>();
            cam.gameObject.SetActive(false);
        }

        // PostureHandler + MetaXRPostureInput
        var postureInput   = playerObj.AddComponent<MetaXRPostureInput>();
        var postureHandler = playerObj.GetComponent<PostureHandler>();
        if (postureHandler != null) postureHandler.Initialize(postureInput);

        // GazeHandler + MetaXRGazeInput
        var gazeInput   = playerObj.AddComponent<MetaXRGazeInput>();
        var gazeHandler = playerObj.GetComponent<GazeHandler>();
        if (gazeHandler != null) gazeHandler.Initialize(gazeInput);

        // Hide own avatar from self
        foreach (var r in playerObj.GetComponentsInChildren<MeshRenderer>(true))
            r.enabled = false;

        // ExperimentManager2
        expMgr.Initialize(isExpert: false);

        // WorkerHUD2
        var hud = playerObj.AddComponent<WorkerHUD2>();
        hud.Initialize(expMgr);

        // Photon Voice 2 — Recorder must be on the prefab; we configure it here
        var recorder = playerObj.GetComponentInChildren<Recorder>();
        if (recorder != null && !string.IsNullOrEmpty(micDevice))
            recorder.MicrophoneDevice = new Photon.Voice.DeviceInfo(micDevice);
        else if (recorder == null)
            Debug.LogWarning("[SceneBootstrapper2] Recorder not found on LocalWorker prefab.");

        if (recorder != null)
        {
            recorder.MicrophoneType = Recorder.MicType.Photon;
            recorder.SetAndroidNativeMicrophoneSettings(aec: true, agc: true, ns: true);
            var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                      ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
            dsp.AEC = false; dsp.NoiseSuppression = false; dsp.AGC = false;
            recorder.SamplingRate  = SamplingRate.Sampling16000;
            recorder.FrameDuration = OpusCodec.FrameDuration.Frame20ms;
            recorder.Bitrate       = 24000;
        }

        // VoiceRecorder — WAV recording independent of PV2
        string logDir = System.IO.Path.Combine(Application.persistentDataPath, "logs", participantId);
        _voiceRecorder = playerObj.AddComponent<VoiceRecorder>();
        _voiceRecorder.Initialize(false, logDir, micDevice);

        // WorkerVideoStream — WebRTC, no transport arg; signaling wired below
        PhotonNetwork.AddCallbackTarget(this);
        _videoStream = playerObj.AddComponent<WorkerVideoStream>();
        _videoStream.Initialize(expMgr);

        var s = _videoStream.Session;
        s.OnSendOffer  += sdp => RaiseSignal(WebRtcVideoSession.EVT_OFFER,  new[] { sdp });
        s.OnSendAnswer += sdp => RaiseSignal(WebRtcVideoSession.EVT_ANSWER, new[] { sdp });
        s.OnSendIce    += (c, mid, idx) => RaiseSignal(WebRtcVideoSession.EVT_ICE, new[] { c, mid, idx.ToString() });

        CheckForExistingExpert();

#if UNITY_ANDROID && !UNITY_EDITOR
        AcquireWifiLock();
#endif
    }

    // ── WebRTC signaling helpers ─────────────────────────────────────────────

    private static void RaiseSignal(byte evtCode, string[] data)
    {
        PhotonNetwork.RaiseEvent(evtCode, data,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
    }

    public void OnEvent(EventData ev)
    {
        switch (ev.Code)
        {
            case WebRtcVideoSession.EVT_ANSWER:
                _videoStream?.Session?.ApplyRemoteAnswer(((string[])ev.CustomData)[0]);
                break;
            case WebRtcVideoSession.EVT_ICE when _videoStream?.Session != null:
            {
                var d = (string[])ev.CustomData;
                if (int.TryParse(d.Length > 2 ? d[2] : "0", out int idx))
                    _videoStream.Session.AddRemoteIce(d[0], d.Length > 1 ? d[1] : "", idx);
                break;
            }
            case WebRtcVideoSession.EVT_OFFER:
                _videoDisplay?.Session?.ApplyRemoteOffer(((string[])ev.CustomData)[0]);
                break;
            case WebRtcVideoSession.EVT_ICE when _videoDisplay?.Session != null:
            {
                var d = (string[])ev.CustomData;
                if (int.TryParse(d.Length > 2 ? d[2] : "0", out int idx))
                    _videoDisplay.Session.AddRemoteIce(d[0], d.Length > 1 ? d[1] : "", idx);
                break;
            }
        }
    }

    private static bool IsExpertReady(Player player) =>
        player.CustomProperties.TryGetValue("expertReady", out var v) && v is bool b && b;

    private void TriggerOfferOnce()
    {
        if (_offerTriggered) return;
        _offerTriggered = true;
        Debug.Log("[SceneBootstrapper2] Triggering WebRTC offer (once).");
        FileLogger.Log("Setup", "[SceneBootstrapper2] Triggering WebRTC offer (once).");
        _videoStream?.TriggerOffer();
    }

    private void CheckForExistingExpert()
    {
        foreach (var player in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(player) != RoleManager.ROLE_EXPERT) continue;
            if (!_expertAudioAttached) { _expertAudioAttached = true; StartCoroutine(WaitForRemoteSpeaker(false)); }
            if (IsExpertReady(player)) TriggerOfferOnce();
            return;
        }
    }

    public override void OnPlayerPropertiesUpdate(Player target, Hashtable changedProps)
    {
        if (_offlineMode || !_setupDone) return;
        if (_role != RoleManager.ROLE_WORKER) return;
        if (RoleManager.GetPlayerRole(target) != RoleManager.ROLE_EXPERT) return;
        if (changedProps.ContainsKey("expertReady") && IsExpertReady(target))
        {
            Debug.Log("[SceneBootstrapper2] Expert signaled ready — triggering WebRTC offer.");
            FileLogger.Log("Setup", "[SceneBootstrapper2] Expert signaled ready — triggering offer.");
            if (!_expertAudioAttached) { _expertAudioAttached = true; StartCoroutine(WaitForRemoteSpeaker(false)); }
            TriggerOfferOnce();
        }
    }

    // ── Expert ────────────────────────────────────────────────────────────

    private void SetupExpert(GameObject playerObj, ExperimentManager2 expMgr, string micDevice)
    {
        // Remove OVRCameraRig — Expert is PC only
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null) { rig.gameObject.SetActive(false); Destroy(rig.gameObject); }

        if (FindAnyObjectByType<AudioListener>() == null)
            new GameObject("AudioListener").AddComponent<AudioListener>();

        if (playerObj.GetComponent<ConnectionHandler>() == null)
            playerObj.AddComponent<ConnectionHandler>();

        // GazeHandler + OscGazeInput
        var gazeInput   = playerObj.AddComponent<OscGazeInput>();
        var gazeHandler = playerObj.GetComponent<GazeHandler>();
        if (gazeHandler != null)
        {
            gazeHandler.Initialize(gazeInput);
            expMgr.SetGazeHandler(gazeHandler);  // condition switches will update gaze mode
        }

        // ExperimentManager2
        expMgr.Initialize(isExpert: true);

        // ExpertUI2
        var ui = playerObj.AddComponent<ExpertUI2>();
        ui.Initialize(expMgr);

        // Photon Voice 2 — Recorder must be on the prefab; configure it here
        var recorder = playerObj.GetComponentInChildren<Recorder>();
        if (recorder != null && !string.IsNullOrEmpty(micDevice))
            recorder.MicrophoneDevice = new Photon.Voice.DeviceInfo(micDevice);
        else if (recorder == null)
            Debug.LogWarning("[SceneBootstrapper2] Recorder not found on RemoteExpert prefab.");

        if (recorder != null)
        {
            var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                      ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
            dsp.AEC = false; dsp.NoiseSuppression = true; dsp.AGC = true;
            dsp.AgcCompressionGain = 18; dsp.AgcTargetLevel = 3;
            recorder.SamplingRate  = SamplingRate.Sampling48000; // PC mic does not support 16000; use 48000
            recorder.FrameDuration = OpusCodec.FrameDuration.Frame20ms;
            recorder.Bitrate       = 24000;
        }

        // VoiceRecorder — WAV recording
        string logDir = System.IO.Path.Combine(Application.persistentDataPath, "logs", participantId);
        _voiceRecorder = playerObj.AddComponent<VoiceRecorder>();
        _voiceRecorder.Initialize(true, logDir, micDevice);
        StartCoroutine(WaitForRemoteSpeaker(true));

        // GazeVisualizer (self-view)
        new GameObject("LocalGazeVisualizer").AddComponent<GazeVisualizer>().Initialize();

        if (!_offlineMode)
        {
            PhotonNetwork.CurrentRoom?.SetCustomProperties(new ExitGames.Client.Photon.Hashtable
            {
                { "participantId", participantId }
            });
        }

        // ExpertVideoDisplay — WebRTC answerer; signaling wired below
        PhotonNetwork.AddCallbackTarget(this);
        _videoDisplay = playerObj.AddComponent<ExpertVideoDisplay>();
        _videoDisplay.Initialize(expMgr);

        var s = _videoDisplay.Session;
        s.OnSendOffer  += sdp => RaiseSignal(WebRtcVideoSession.EVT_OFFER,  new[] { sdp });
        s.OnSendAnswer += sdp => RaiseSignal(WebRtcVideoSession.EVT_ANSWER, new[] { sdp });
        s.OnSendIce    += (c, mid, idx) => RaiseSignal(WebRtcVideoSession.EVT_ICE, new[] { c, mid, idx.ToString() });

        // Signal Worker that signaling is ready — Worker waits for this before calling TriggerOffer()
        if (!_offlineMode)
        {
            Debug.Log("[SceneBootstrapper2] Setting expertReady=true — Worker can now send WebRTC offer.");
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { ["expertReady"] = true });
        }

        // ExperimentLogger — writes trials.csv / frames.csv / replay JSON
        string logDir2 = System.IO.Path.Combine(
            Application.persistentDataPath, "logs", participantId);
        var logger = playerObj.AddComponent<ExperimentLogger>();
        logger.Initialize(expMgr, participantOrderIndex, logDir2);
    }

    // ── Photon callbacks ──────────────────────────────────────────────────

    public override void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable changedProps)
    {
        if (!_setupDone || _offlineMode) return;
        if (_role != RoleManager.ROLE_WORKER) return;
        if (!changedProps.ContainsKey("participantId")) return;

        var val = changedProps["participantId"] as string;
        if (val == null) return;
        participantId = val;
        var expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (expMgr != null) expMgr.participantId = participantId;
        FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker received participantId={participantId} from room properties.");
    }

    // ── Disconnect ────────────────────────────────────────────────────────

    private void OnPhotonDisconnected()
    {
        if (!_setupDone) return;
        FileLogger.Log("Setup", "[SceneBootstrapper2] Disconnected — reset for reconnect.");
        _setupDone = false;
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (!_setupDone) return;
        if (_role == RoleManager.ROLE_EXPERT
            && RoleManager.GetPlayerRole(newPlayer) == RoleManager.ROLE_WORKER)
        {
            StartCoroutine(WaitForRemoteSpeaker(true));
        }
    }

    private Coroutine _speakerSearchCoroutine;

    private IEnumerator WaitForRemoteSpeaker(bool isExpert)
    {
        if (_speakerSearchCoroutine != null)
            StopCoroutine(_speakerSearchCoroutine);
        _speakerSearchCoroutine = null;

        // Bounded wait: a remote PhotonVoiceView Speaker normally appears within a few seconds
        // of both peers joining. Without a timeout this coroutine would spin forever (and leak)
        // if the remote never publishes a Speaker — masking the real failure instead of surfacing it.
        float elapsed = 0f;
        const float timeout = 30f;
        while (elapsed < timeout)
        {
            foreach (var pvv in FindObjectsByType<PhotonVoiceView>(FindObjectsSortMode.None))
            {
                if (pvv.GetComponent<PhotonView>()?.IsMine == false && pvv.SpeakerInUse != null)
                {
                    var src = pvv.SpeakerInUse.GetComponent<AudioSource>();
                    if (src != null)
                    {
                        if (isExpert) { src.volume = 3f; src.spatialBlend = 0f; }
                        else          { src.volume = 1f; src.spatialBlend = 1f;
                                        src.rolloffMode = AudioRolloffMode.Linear;
                                        src.minDistance = 1f; src.maxDistance = 20f; }
                    }
                    _voiceRecorder?.AttachRemoteCapture(pvv.SpeakerInUse);
                    _speakerSearchCoroutine = null;
                    yield break;
                }
            }
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        _speakerSearchCoroutine = null;
        Debug.LogWarning($"[SceneBootstrapper2] Remote Speaker not found within {timeout:F0}s — remote audio capture not started. Check that the remote peer's PunVoiceClient/Recorder is transmitting.");
        FileLogger.Log("Setup", "[SceneBootstrapper2] WaitForRemoteSpeaker timed out.");
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);

        if (networkManager != null)
        {
            networkManager.OnRoomJoined          -= OnRoomJoined;
            networkManager.OnNetworkDisconnected -= OnPhotonDisconnected;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        try { _wifiLock?.Call("release"); } catch { }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _wifiLock;

    private void AcquireWifiLock()
    {
        try
        {
            using var player   = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var wifiMgr  = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
            _wifiLock = wifiMgr.Call<AndroidJavaObject>("createWifiLock", 4, "CoGaze_RealTimeAV");
            _wifiLock.Call("acquire");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SceneBootstrapper2] WiFi lock: {ex.Message}");
        }
    }
#endif
}
