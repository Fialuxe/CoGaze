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

// Entry point for ExperimentScene (10-condition design); uses ExperimentManager2, WorkerHUD2, ExpertUI2.
public class SceneBootstrapper2 : MonoBehaviourPunCallbacks, IOnEventCallback
{
    [Header("Participant")]
    [Tooltip("Williams table row (0-9).")]
    public int    participantOrderIndex = 0;
    public string participantId        = "P00";

    [Header("Setup")]
    [Tooltip("Total number of task QR markers in the room. Their ids are assumed to be single " +
             "letters 'A'..('A'+count-1). All must be present (auto-detected or manually registered " +
             "via controller grip) before the Expert can approve.")]
    public int requiredTaskQRCount = 5;

#if UNITY_EDITOR
    [Header("Editor Debug")]
    [Tooltip("Force Worker role in Play Mode for testing without Android build.")]
    [SerializeField] private bool _editorForceWorkerRole;
#endif

    private const string k_workerPrefab = "Prefabs/LocalWorker";
    private const string k_expertPrefab = "Prefabs/RemoteExpert";

    private NetworkManager     _networkManager;
    private string             _role;
    private bool               _setupDone;
    private bool               _offlineMode;
    private string             _selectedMic      = "";
    private int                _startConditionOffset;   // resume: completed conditions to skip (Expert-authoritative)
    private WorkerVideoStream  _videoStream;      // Worker side — for WebRTC signaling
    private ExpertVideoDisplay _videoDisplay;     // Expert side — for WebRTC signaling
    private VoiceRecorder      _voiceRecorder;
    private bool               _offerTriggered;
    private bool               _expertAudioAttached;
    private SetupCoordinator   _workerSetupCoord;          // Worker side — routes Expert setup-readiness to its panel
    private bool?              _publishedExpertSetupReady;  // Expert side — last published "expertSetupReady" (idempotency)

    // ── Awake ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Keep the headset / display awake for the whole session: without this the Quest can self-sleep
        // during a long step (e.g. the 180s assembly), and proximity-sensor / OS suspend then drops Photon
        // and rolls the Worker back to "setup". Idempotent; harmless on PC.
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        // SpeakerPrefab をルーム参加より前に修正する。OnRoomJoined でも設定しているが、
        // リモートプレイヤーの PhotonVoiceView.Start() が同フレームに走ると間に合わないため
        // ここで早期に上書きする。
        FixSpeakerPrefab();

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
        _networkManager = nmObj.AddComponent<NetworkManager>();
        _networkManager.OnRoomJoined          += OnRoomJoined;
        _networkManager.OnNetworkDisconnected += OnPhotonDisconnected;
        StartCoroutine(StartupFlow());
        FileLogger.Log("Setup", "[SceneBootstrapper2] Starting startup flow...");
    }

    // ── StartupFlow ────────────────────────────────────────────────────────

    private IEnumerator StartupFlow()
    {
        var config = StartupConfig.LoadOrDefault();

        // Config is pre-written to disk via StartupConfig.Save() on the PC side first.
        if (Application.platform != RuntimePlatform.Android)
        {
            // Expert (PC/standalone): IMGUI config panel + self-check, blocks Start on fatal issues.
            bool confirmed = false;
            var ui = gameObject.AddComponent<StartupUI>();
            ui.Initialize(config);
            ui.OnConfirmed += () => confirmed = true;
            yield return new WaitUntil(() => confirmed);
        }
        else
        {
            // Worker (Quest/HMD): WorldSpace startup panel + self-check, confirmed with the right-hand
            // A button (was a headless auto-proceed). Fatal checks block the confirm.
            var panel = gameObject.AddComponent<WorkerStartupPanel>();
            panel.Initialize(config);
            // Lobby-only connection in parallel so the panel can show Photon reachability and
            // whether the Expert's room already exists. The room itself is joined only after
            // the A-button confirm (JoinExperimentRoom below).
            if (!config.offlineMode)
                _networkManager.ConnectForRoomPreview();
            yield return new WaitUntil(() => panel.Confirmed);
            Destroy(panel);
        }

        // Apply config
        participantId         = config.participantId;
        participantOrderIndex = config.participantOrderIndex;
        _startConditionOffset = config.startConditionOffset;
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
            // Joins the room from any prior state — fresh boot (Expert) or the Worker's
            // lobby-preview connection started alongside the startup panel.
            _networkManager.JoinExperimentRoom();
        }
    }

    // SpeakerPrefab の型不一致 (InvalidCastException in InstantiateSpeakerPrefab) を防ぐ。
    // PunVoiceClient の Inspector 参照が誤型になっていることがあるため、Resources から上書きする。
    // Awake と OnRoomJoined の両方から呼ぶことでタイミング問題を回避。
    private void FixSpeakerPrefab()
    {
        var pvc = FindAnyObjectByType<PunVoiceClient>();
        if (pvc == null) return;
        var spk = Resources.Load<GameObject>("Speaker");
        if (spk != null)
        {
            pvc.SpeakerPrefab = spk;
            FileLogger.Log("Setup", "[SceneBootstrapper2] PunVoiceClient.SpeakerPrefab assigned from Resources/Speaker.");
        }
        else
        {
            FileLogger.Log("Setup", "[SceneBootstrapper2] Resources.Load<GameObject>(\"Speaker\") returned null; voice playback will be silent.");
        }
    }

    // ── OnRoomJoined ──────────────────────────────────────────────────────

    private void OnRoomJoined()
    {
        DetectRole();
        RoleManager.SetRole(_role);
        ConfigureXR(_role);

        // Connect PunVoiceClient after PUN room join to avoid "Provide an AppId" error
        // that occurs when AutoConnectAndJoin fires before Photon Realtime is connected.
        FixSpeakerPrefab(); // Awake でも設定済みだが念のため再設定
        var pvc = FindAnyObjectByType<PunVoiceClient>();
        if (pvc != null && !pvc.Client.IsConnected)
        {
            FileLogger.Log("Setup", "[SceneBootstrapper2] Connecting PunVoiceClient after room join.");
            pvc.ConnectAndJoinRoom();
        }

        // Periodic position logging (head / players / SharedMesh / QR markers) for offline debug.
        if (GetComponent<PositionLogger>() == null) gameObject.AddComponent<PositionLogger>();

        // The Worker may have completed dual-QR calibration BEFORE joining (the startup panel delays
        // the join while MRUK auto-detects the calib QRs). Those one-shot mesh/calib RPCs were sent
        // with no room and lost — re-broadcast them now so the Expert's SharedMesh aligns and the
        // approve gate isn't deadlocked. No-op on the Expert (its MeshHandler isn't calibrated).
        if (_role == RoleManager.ROLE_WORKER)
        {
            var mh = FindAnyObjectByType<MeshHandler>();
            if (mh != null) mh.RebroadcastCalibration();
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

    private string ResolveMicDevice()
    {
        var devices = Microphone.devices;
        if (devices.Length == 0) return "";

        if (Application.platform == RuntimePlatform.Android)
            return devices[0];

        if (!string.IsNullOrEmpty(_selectedMic) && System.Array.IndexOf(devices, _selectedMic) >= 0)
            return _selectedMic;

        return devices[0];
    }

    private IEnumerator SetupAfterDeviceCheck()
    {
        string mic = ResolveMicDevice();
        FileLogger.Log("Setup", $"[SceneBootstrapper2] Mic: '{(mic == "" ? "(default)" : mic)}'");

        bool isExpert = _role == RoleManager.ROLE_EXPERT;

        var expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (expMgr == null)
        {
            Debug.LogError("[SceneBootstrapper2] ExperimentManager2 not found in scene.");
            yield break;
        }

        // The Expert publishes the authoritative participant identity as room properties. When the
        // Worker joins AFTER they were set (PC started first — the usual order), PUN2 never fires
        // OnRoomPropertiesUpdate for pre-existing properties, so read them directly here. Otherwise
        // the rig (VoiceRecorder log dir, QuestionnaireManager identity) is built with the stale
        // on-device config values (typically P00 / index 0).
        if (!isExpert && !_offlineMode && PhotonNetwork.CurrentRoom != null)
        {
            var roomProps = PhotonNetwork.CurrentRoom.CustomProperties;
            if (roomProps.TryGetValue("participantId", out object pidObj) &&
                pidObj is string roomPid && !string.IsNullOrWhiteSpace(roomPid))
            {
                FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker read participantId={roomPid} from room properties at join.");
                ApplyWorkerParticipantId(roomPid);
            }
            if (roomProps.TryGetValue("participantOrderIndex", out object oiObj) && oiObj is int roomOi)
            {
                FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker read participantOrderIndex={roomOi} from room properties at join.");
                ApplyWorkerParticipantOrderIndex(roomOi);
            }
            if (roomProps.TryGetValue("startConditionOffset", out object soObj) && soObj is int roomSo)
            {
                FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker read startConditionOffset={roomSo} from room properties at join.");
                ApplyWorkerStartConditionOffset(roomSo);
            }
        }

        expMgr.participantOrderIndex = participantOrderIndex;
        expMgr.participantNumber     = participantOrderIndex;  // was unset → BuildConditionOrder logged "P0"
        expMgr.participantId         = participantId;
        expMgr.startConditionOffset  = _startConditionOffset;  // before Initialize → ExpandTemplate

        GameObject playerObj;
        if (_offlineMode)
        {
            // Offline: instantiate locally, skip Photon ownership check
            var prefab = Resources.Load<GameObject>(isExpert ? k_expertPrefab : k_workerPrefab);
            if (prefab == null)
            {
                Debug.LogError($"[SceneBootstrapper2] Prefab not found: {(isExpert ? k_expertPrefab : k_workerPrefab)}");
                yield break;
            }
            playerObj = Instantiate(prefab, Vector3.zero, Quaternion.identity);
        }
        else
        {
            playerObj = PhotonNetwork.Instantiate(
                isExpert ? k_expertPrefab : k_workerPrefab, Vector3.zero, Quaternion.identity);
            if (playerObj == null)
            {
                Debug.LogError($"[SceneBootstrapper2] PhotonNetwork.Instantiate returned null for {(isExpert ? k_expertPrefab : k_workerPrefab)}");
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
        PhotonNetwork.AddCallbackTarget(this);
        var r = WorkerRigBuilder.Build(
            playerObj, expMgr, micDevice,
            participantId, participantOrderIndex, requiredTaskQRCount,
            RaiseSignal);
        _videoStream      = r.VideoStream;
        _voiceRecorder    = r.VoiceRecorder;
        _workerSetupCoord = r.SetupCoordinator;
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
        // CustomData arrives from a remote peer over the wire — never trust its type or length.
        // A malformed payload (null, wrong type, or too few elements) must not throw inside the
        // Photon event pump; an unhandled exception there would kill all further signaling.
        var d = ev.CustomData as string[];
        if (d == null || d.Length == 0) return;

        switch (ev.Code)
        {
            case WebRtcVideoSession.EVT_ANSWER:
                _videoStream?.Session?.ApplyRemoteAnswer(d[0]);
                break;
            case WebRtcVideoSession.EVT_ICE when _videoStream?.Session != null:
                if (int.TryParse(d.Length > 2 ? d[2] : "0", out int idx))
                    _videoStream.Session.AddRemoteIce(d[0], d.Length > 1 ? d[1] : "", idx);
                break;
            case WebRtcVideoSession.EVT_OFFER:
                _videoDisplay?.Session?.ApplyRemoteOffer(d[0]);
                break;
            case WebRtcVideoSession.EVT_ICE when _videoDisplay?.Session != null:
                if (int.TryParse(d.Length > 2 ? d[2] : "0", out int idx2))
                    _videoDisplay.Session.AddRemoteIce(d[0], d.Length > 1 ? d[1] : "", idx2);
                break;
        }
    }

    private static bool IsExpertReady(Player player) =>
        player.CustomProperties.TryGetValue("expertReady", out var v) && v is bool b && b;

    private static bool GetExpertSetupReady(Player player) =>
        player.CustomProperties.TryGetValue("expertSetupReady", out var v) && v is bool b && b;

    // Worker side: forward the Expert's granular setup-readiness to the Worker's setup panel.
    private void ApplyExpertSetupReady(bool ready) => _workerSetupCoord?.SetExpertSetupReady(ready);

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
            if (!_expertAudioAttached) { _expertAudioAttached = true; StartSpeakerSearch(false); }
            if (IsExpertReady(player)) TriggerOfferOnce();
            // Seed the Expert's setup-readiness in case it was published before the Worker joined.
            ApplyExpertSetupReady(GetExpertSetupReady(player));
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
            if (!_expertAudioAttached) { _expertAudioAttached = true; StartSpeakerSearch(false); }
            TriggerOfferOnce();
        }
        // Mirror the Expert's granular setup-readiness onto the Worker's setup panel.
        if (changedProps.TryGetValue("expertSetupReady", out var sr) && sr is bool srb)
            ApplyExpertSetupReady(srb);
    }

    private IEnumerator PublishExpertSetupReadyLoop(ExperimentManager2 expMgr)
    {
        var wait = new WaitForSeconds(0.5f);
        while (expMgr != null)
        {
            bool ready = expMgr.IsExpertSelfReady;
            if (_publishedExpertSetupReady != ready)
            {
                _publishedExpertSetupReady = ready;
                PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { ["expertSetupReady"] = ready });
                FileLogger.Log("Setup", $"[SceneBootstrapper2] Published expertSetupReady={ready}.");
            }
            if (ready) yield break;   // monotonic — final value sent; stop polling
            yield return wait;
        }
    }

    // ── Expert ────────────────────────────────────────────────────────────

    private void SetupExpert(GameObject playerObj, ExperimentManager2 expMgr, string micDevice)
    {
        PhotonNetwork.AddCallbackTarget(this);
        var r = ExpertRigBuilder.Build(
            playerObj, expMgr, micDevice,
            participantId, participantOrderIndex, requiredTaskQRCount,
            RaiseSignal, _offlineMode);
        // Publish the resume offset with the other identity props (always, even 0, so the Worker's
        // value is deterministic regardless of what a previous session left behind).
        if (!_offlineMode)
            PhotonNetwork.CurrentRoom?.SetCustomProperties(
                new Hashtable { ["startConditionOffset"] = _startConditionOffset });
        _videoDisplay  = r.VideoDisplay;
        _voiceRecorder = r.VoiceRecorder;
        StartSpeakerSearch(true);
        // Publish granular self-readiness so the Worker's setup panel shows 実験者 準備中/準備完了.
        if (!_offlineMode)
            StartCoroutine(PublishExpertSetupReadyLoop(expMgr));
    }

    // ── Photon callbacks ──────────────────────────────────────────────────

    public override void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable changedProps)
    {
        // No _setupDone gate: this handler only assigns identity fields, which is safe (and
        // necessary) even while the rig is still being built — dropping an early update here
        // would leave the Worker on its stale local id.
        if (_offlineMode) return;
        if (_role != RoleManager.ROLE_WORKER) return;

        if (changedProps.TryGetValue("participantId", out object idObj) &&
            idObj is string id && !string.IsNullOrWhiteSpace(id))
        {
            FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker received participantId={id} from room properties.");
            ApplyWorkerParticipantId(id);
        }
        if (changedProps.TryGetValue("participantOrderIndex", out object oiObj) && oiObj is int oi)
        {
            FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker received participantOrderIndex={oi} from room properties.");
            ApplyWorkerParticipantOrderIndex(oi);
        }
        if (changedProps.TryGetValue("startConditionOffset", out object soObj) && soObj is int so)
        {
            FileLogger.Log("Setup", $"[SceneBootstrapper2] Worker received startConditionOffset={so} from room properties.");
            ApplyWorkerStartConditionOffset(so);
        }
    }

    // Worker: adopt the Expert-synced participantId into this bootstrapper, ExperimentManager2,
    // QuestionnaireManager (its JSON save path is computed lazily on first submit — the synced id
    // must be set before then), and the on-device config, so the next boot's startup panel shows
    // the last real id instead of a stale default (P00).
    private void ApplyWorkerParticipantId(string id)
    {
        participantId = id;
        var expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (expMgr != null) expMgr.participantId = id;
        var qm = FindAnyObjectByType<QuestionnaireManager>();
        if (qm != null) qm.participantId = id;
        var cfg = StartupConfig.LoadOrDefault();
        if (cfg.participantId != id)
        {
            cfg.participantId = id;
            cfg.Save();
        }
    }

    // Worker: adopt the Expert's resume offset. Both sides must build the SAME step list — step
    // indices travel in the Expert's state broadcasts and the Worker reads its instruction text
    // from _steps[index] — so rebuild if the template was already expanded (worker-first join).
    // Session-scoped by design: never persisted to the on-device config.
    private void ApplyWorkerStartConditionOffset(int off)
    {
        _startConditionOffset = off;
        var expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (expMgr != null && expMgr.startConditionOffset != off)
        {
            expMgr.startConditionOffset = off;
            expMgr.RebuildStepsForOffset();
        }
    }

    // Worker: adopt the Expert-synced order index. On the Worker this value is only identity
    // metadata (questionnaire fallback id, log lines) — condition sequencing always follows the
    // Expert's state broadcasts — but keeping it synced makes the Expert the single authority.
    private void ApplyWorkerParticipantOrderIndex(int idx)
    {
        idx = Mathf.Clamp(idx, 0, 23);
        participantOrderIndex = idx;
        var expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (expMgr != null)
        {
            expMgr.participantOrderIndex = idx;
            expMgr.participantNumber     = idx;
        }
        var qm = FindAnyObjectByType<QuestionnaireManager>();
        if (qm != null) qm.participantNumber = idx;
        var cfg = StartupConfig.LoadOrDefault();
        if (cfg.participantOrderIndex != idx)
        {
            cfg.participantOrderIndex = idx;
            cfg.Save();
        }
    }

    // ── Disconnect ────────────────────────────────────────────────────────

    private void OnPhotonDisconnected()
    {
        if (!_setupDone) return;
        FileLogger.Log("Setup", "[SceneBootstrapper2] Disconnected — reset for reconnect.");
        _setupDone = false;

        // Reset the one-shot guards so the rejoin path (OnRoomJoined → SetupAfterDeviceCheck →
        // SetupWorker/SetupExpert) re-establishes A/V idempotently. Without this:
        //  - _offerTriggered stayed true   → the WebRTC video offer was never re-sent → no video.
        //  - _expertAudioAttached stayed true → the remote-Speaker search never restarted → no audio.
        //  - _publishedExpertSetupReady kept its last value → Expert never re-published readiness.
        // SetupWorker re-creates _videoStream and calls CheckForExistingExpert, which re-fires the
        // offer (now that _offerTriggered is clear) once the Expert is present/ready again.
        _offerTriggered            = false;
        _expertAudioAttached       = false;
        _publishedExpertSetupReady = null;
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (!_setupDone) return;
        if (_role == RoleManager.ROLE_EXPERT
            && RoleManager.GetPlayerRole(newPlayer) == RoleManager.ROLE_WORKER)
        {
            StartSpeakerSearch(true);
        }
    }

    private Coroutine _speakerSearchCoroutine;

    // Single owner of _speakerSearchCoroutine: cancel any in-flight search before starting a new
    // one. The previous code called StartCoroutine(WaitForRemoteSpeaker(...)) directly without
    // storing the returned handle, so the cancel-guard never fired — reconnects could spawn
    // several overlapping searches that each re-tuned the remote Speaker's volume/spatialBlend.
    private void StartSpeakerSearch(bool isExpert)
    {
        if (_speakerSearchCoroutine != null)
            StopCoroutine(_speakerSearchCoroutine);
        _speakerSearchCoroutine = StartCoroutine(WaitForRemoteSpeaker(isExpert));
    }

    private IEnumerator WaitForRemoteSpeaker(bool isExpert)
    {
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
                        else          { src.volume = 3f; src.spatialBlend = 1f;
                                        src.rolloffMode = AudioRolloffMode.Linear;
                                        src.minDistance = 1f; src.maxDistance = 20f; }
                    }
                    _voiceRecorder?.AttachRemoteCapture(pvv.SpeakerInUse);
                    yield break;
                }
            }
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        Debug.LogWarning($"[SceneBootstrapper2] Remote Speaker not found within {timeout:F0}s — remote audio capture not started. Check that the remote peer's PunVoiceClient/Recorder is transmitting.");
        FileLogger.Log("Setup", "[SceneBootstrapper2] WaitForRemoteSpeaker timed out.");
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);

        if (_networkManager != null)
        {
            _networkManager.OnRoomJoined          -= OnRoomJoined;
            _networkManager.OnNetworkDisconnected -= OnPhotonDisconnected;
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
