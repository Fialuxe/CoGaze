using System;
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using Photon.Voice;
using Photon.Voice.Unity;
using Photon.Voice.PUN;
using POpusCodec.Enums;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

/// <summary>
/// LocalWorker (Quest) setup.
/// - Instantiates LocalWorker prefab and attaches all handlers.
/// - Configures Photon Voice 2 Recorder with the selected mic device.
/// - Wires WebRTC signaling (via Photon RaiseEvent) between WorkerVideoStream and Expert.
/// - Attaches VoiceRecorder for WAV recording.
/// </summary>
public class LocalWorkerSetup : MonoBehaviourPunCallbacks, IOnEventCallback
{
    private const string PREFAB_PATH            = "Prefabs/LocalWorker";
    private const string GAZE_VISUALIZER_PREFAB = "Prefabs/GazeVisualizer";

    public int    participantNumber  = 0;
    public string preferredMicDevice = null;

    private GameObject         localWorkerInstance;
    private PhotonView         localWorkerView;
    private GameObject         gazeVisualizerInstance;
    private ExperimentManager2 expManager;
    private WorkerVideoStream  videoStream;
    private VoiceRecorder      voiceRecorder;
    // Guards against sending a second Offer if both OnPlayerEnteredRoom and OnPlayerPropertiesUpdate
    // fire in the same frame (e.g. Expert already in room when Worker joins).
    private bool               _offerTriggered;
    private bool               _expertAudioAttached;

#if UNITY_ANDROID && !UNITY_EDITOR
    // WIFI_MODE_FULL_LOW_LATENCY (4, Android 10+) disables the WiFi PSM (power-saving mode).
    // Without this lock the AP can batch-deliver packets at the DTIM interval (up to 500 ms),
    // which is the single largest source of audio dropout on Quest even on a strong signal.
    private AndroidJavaObject _wifiLock;
#endif

    public void Initialize()
    {
        Camera existingCam = Camera.main;
        if (existingCam != null && existingCam.GetComponentInParent<OVRCameraRig>() == null)
        {
            existingCam.gameObject.SetActive(false);
            FileLogger.Log("Setup", "[LocalWorkerSetup] Default Main Camera disabled.");
        }

        OVRCameraRig existingRig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
        if (existingRig == null)
            Debug.LogWarning("[LocalWorkerSetup] OVRCameraRig not found. Place OVRCameraRigSetup prefab in scene.");

        localWorkerInstance = PhotonNetwork.Instantiate(PREFAB_PATH, Vector3.zero, Quaternion.identity);
        if (localWorkerInstance == null)
        {
            Debug.LogError("[LocalWorkerSetup] PhotonNetwork.Instantiate returned null for LocalWorker prefab.");
            return;
        }
        localWorkerView = localWorkerInstance.GetComponent<PhotonView>();
        if (localWorkerView == null)
        {
            Debug.LogError("[LocalWorkerSetup] PhotonView missing on instantiated LocalWorker prefab.");
            return;
        }

        if (localWorkerView.IsMine)
        {
            // PostureHandler + MetaXRPostureInput
            var postureInput   = localWorkerInstance.AddComponent<MetaXRPostureInput>();
            var postureHandler = localWorkerInstance.GetComponent<PostureHandler>();
            if (postureHandler != null) postureHandler.Initialize(postureInput);
            else Debug.LogError("[LocalWorkerSetup] PostureHandler missing.");

            // GazeHandler + MetaXRGazeInput
            var gazeInput   = localWorkerInstance.AddComponent<MetaXRGazeInput>();
            var gazeHandler = localWorkerInstance.GetComponent<GazeHandler>();
            if (gazeHandler != null) gazeHandler.Initialize(gazeInput);
            else Debug.LogError("[LocalWorkerSetup] GazeHandler missing.");

            // MeshHandler
            if (localWorkerInstance.GetComponent<MeshHandler>() == null)
                Debug.LogError("[LocalWorkerSetup] MeshHandler missing.");

            // Hide own avatar from self
            foreach (var r in localWorkerInstance.GetComponentsInChildren<MeshRenderer>(true))
                r.enabled = false;

            // ExperimentManager2 — Worker is a mirror receiver
            expManager = localWorkerInstance.AddComponent<ExperimentManager2>();
            expManager.participantNumber = participantNumber;
            expManager.Initialize(isExpert: false);

#if UNITY_ANDROID && !UNITY_EDITOR
            // WorkerHandBroadcaster — sends hand bone positions to Expert for logging
            try
            {
                var handBc = localWorkerInstance.AddComponent<WorkerHandBroadcaster>();
                handBc.Initialize(expManager);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LocalWorkerSetup] WorkerHandBroadcaster failed: {ex.Message}");
            }
#endif

            // WorkerHUD2 — world-space overlay in the HMD
            var hud = localWorkerInstance.AddComponent<WorkerHUD2>();
            hud.Initialize(expManager);

            var meshHandler = localWorkerInstance.GetComponent<MeshHandler>();
            hud.ConnectMeshHandler(meshHandler);

            var idTask = UnityEngine.Object.FindAnyObjectByType<IdentificationTask>();
            hud.ConnectIdentificationTask(idTask);

            // Photon Voice 2 — set preferred mic on the Recorder already on the prefab
            var recorder = localWorkerInstance.GetComponentInChildren<Recorder>();
            if (recorder != null && !string.IsNullOrEmpty(preferredMicDevice))
                recorder.MicrophoneDevice = new Photon.Voice.DeviceInfo(preferredMicDevice);
            else if (recorder == null)
                Debug.LogWarning("[LocalWorkerSetup] Recorder not found on LocalWorker prefab — add PhotonVoiceView + Recorder in the Inspector.");

            if (recorder != null)
            {
                // Use Photon mic type so Android hardware AEC/AGC/NS actually activate.
                recorder.MicrophoneType = Recorder.MicType.Photon;
                recorder.SetAndroidNativeMicrophoneSettings(aec: true, agc: true, ns: true);

                // Hardware AEC/AGC/NS already active — disable SW duplicates to avoid
                // "underwater" timbre (double-NS) and gain-pumping (double-AGC).
                var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                          ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
                dsp.AEC              = false;
                dsp.NoiseSuppression = false;
                dsp.AGC              = false;

                // 16 kHz = native WebRTC DSP rate; 20 ms frame saves 20 ms one-way latency.
                recorder.SamplingRate  = SamplingRate.Sampling16000;
                recorder.FrameDuration = OpusCodec.FrameDuration.Frame20ms;
                recorder.Bitrate       = 24000;
            }

            // VoiceRecorder — WAV recording independent of PV2
            string logDir = System.IO.Path.Combine(
                Application.persistentDataPath, "logs", $"P{participantNumber}");
            voiceRecorder = localWorkerInstance.AddComponent<VoiceRecorder>();
            voiceRecorder.Initialize(false, logDir, preferredMicDevice);

            // VideoStream — WebRTC, no transport arg; signaling wired below
            PhotonNetwork.AddCallbackTarget(this);
            videoStream = localWorkerInstance.AddComponent<WorkerVideoStream>();
            videoStream.Initialize(expManager);

            // Wire WebRTC signaling: session events → Photon RaiseEvent
            var s = videoStream.Session;
            s.OnSendOffer  += sdp => RaiseSignal(WebRtcVideoSession.EVT_OFFER,  new[] { sdp });
            s.OnSendAnswer += sdp => RaiseSignal(WebRtcVideoSession.EVT_ANSWER, new[] { sdp });
            s.OnSendIce    += (c, mid, idx) =>
                RaiseSignal(WebRtcVideoSession.EVT_ICE, new[] { c, mid, idx.ToString() });

            CheckForExistingExpert();

            FileLogger.Log("Setup", "[LocalWorkerSetup] LocalWorker fully initialized.");

#if UNITY_ANDROID && !UNITY_EDITOR
            AcquireWifiLock();
#endif
        }
    }

    // ── WebRTC signaling helpers ─────────────────────────────────────────────

    private static void RaiseSignal(byte evtCode, string[] data)
    {
        PhotonNetwork.RaiseEvent(evtCode, data,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
    }

    // IOnEventCallback — receives signaling from Expert
    public void OnEvent(EventData ev)
    {
        var session = videoStream?.Session;
        if (session == null) return;

        switch (ev.Code)
        {
            case WebRtcVideoSession.EVT_ANSWER:
                Debug.Log("[LocalWorkerSetup] WebRTC answer received from Expert — ICE negotiation starting.");
                session.ApplyRemoteAnswer(((string[])ev.CustomData)[0]);
                break;
            case WebRtcVideoSession.EVT_ICE:
            {
                var d = (string[])ev.CustomData;
                if (int.TryParse(d.Length > 2 ? d[2] : "0", out int idx))
                    session.AddRemoteIce(d[0], d.Length > 1 ? d[1] : "", idx);
                break;
            }
        }
    }

    // ── Room callbacks ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by SceneBootstrapper after re-joining the room following a disconnect.
    /// </summary>
    public void RequestStateSync()
    {
        if (expManager == null)
        {
            Debug.LogWarning("[LocalWorkerSetup] RequestStateSync: expManager is null. Was Initialize called?");
            return;
        }
        PhotonNetwork.AddCallbackTarget(expManager);
        expManager.SendSyncRequest();
        FileLogger.Log("Setup", "[LocalWorkerSetup] RequestStateSync sent.");
    }

    private static bool IsExpertReady(Player player) =>
        player.CustomProperties.TryGetValue("expertReady", out var v) && v is bool b && b;

    private void TriggerOfferOnce()
    {
        if (_offerTriggered) return;
        _offerTriggered = true;
        FileLogger.Log("Setup", "[LocalWorkerSetup] Triggering WebRTC offer (once).");
        videoStream?.TriggerOffer();
    }

    private void TryAttachRemoteCaptureOnce()
    {
        if (_expertAudioAttached) return;
        _expertAudioAttached = true;
        TryAttachRemoteCaptureToExpert();
    }

    private void CheckForExistingExpert()
    {
        foreach (var player in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(player) == RoleManager.ROLE_EXPERT)
            {
                SpawnGazeVisualizer();
                TryAttachRemoteCaptureOnce();
                if (IsExpertReady(player))
                {
                    FileLogger.Log("Setup", "[LocalWorkerSetup] Expert already ready — triggering offer.");
                    TriggerOfferOnce();
                }
                else
                {
                    Debug.Log("[LocalWorkerSetup] Expert in room but not yet ready — waiting for expertReady property.");
                }
                return;
            }
        }
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        StartCoroutine(WaitForRoleAndAct(newPlayer));
    }

    public override void OnPlayerPropertiesUpdate(Player target, Hashtable changedProps)
    {
        if (RoleManager.GetPlayerRole(target) != RoleManager.ROLE_EXPERT) return;
        if (changedProps.ContainsKey("expertReady") && IsExpertReady(target))
        {
            FileLogger.Log("Setup", "[LocalWorkerSetup] Expert signaled ready — triggering offer.");
            SpawnGazeVisualizer();
            TryAttachRemoteCaptureOnce();
            TriggerOfferOnce();
        }
    }

    private IEnumerator WaitForRoleAndAct(Player player)
    {
        float t = 0f;
        while (t < 5f)
        {
            if (RoleManager.GetPlayerRole(player) == RoleManager.ROLE_EXPERT)
            {
                SpawnGazeVisualizer();
                TryAttachRemoteCaptureOnce();
                if (IsExpertReady(player))
                {
                    FileLogger.Log("Setup", "[LocalWorkerSetup] Expert joined and is ready — triggering offer.");
                    TriggerOfferOnce();
                }
                else
                {
                    Debug.Log("[LocalWorkerSetup] Expert joined but not yet ready — waiting for expertReady property.");
                }
                yield break;
            }
            t += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }
        Debug.LogWarning($"[LocalWorkerSetup] Role timeout for {player.NickName}.");
    }

    private void TryAttachRemoteCaptureToExpert()
    {
        if (voiceRecorder == null) return;
        StartCoroutine(WaitForExpertSpeaker());
    }

    private IEnumerator WaitForExpertSpeaker()
    {
        // Bounded wait — without a timeout this spins forever if the Expert never publishes a
        // Speaker (e.g. Expert mic muted / PV2 not transmitting), leaking the coroutine silently.
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
                        src.volume       = 1f;
                        src.spatialBlend = 1f;
                        src.rolloffMode  = AudioRolloffMode.Linear;
                        src.minDistance  = 1f;
                        src.maxDistance  = 20f;
                    }
                    voiceRecorder.AttachRemoteCapture(pvv.SpeakerInUse);
                    yield break;
                }
            }
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        Debug.LogWarning($"[LocalWorkerSetup] Expert Speaker not found within {timeout:F0}s — remote audio capture not started.");
        FileLogger.Log("Setup", "[LocalWorkerSetup] WaitForExpertSpeaker timed out.");
    }

    private void SpawnGazeVisualizer()
    {
        if (gazeVisualizerInstance != null) return;
        gazeVisualizerInstance = new GameObject("LocalGazeVisualizer");
        gazeVisualizerInstance.AddComponent<GazeVisualizer>().Initialize();
        FileLogger.Log("Setup", "[LocalWorkerSetup] GazeVisualizer spawned.");
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
#if UNITY_ANDROID && !UNITY_EDITOR
        ReleaseWifiLock();
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void AcquireWifiLock()
    {
        try
        {
            using var player   = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var wifiMgr  = activity.Call<AndroidJavaObject>("getSystemService", "wifi");

            // WIFI_MODE_FULL_LOW_LATENCY = 4 (Android 10+).
            // Falls back gracefully: if the screen turns off the lock automatically
            // downgrades to FULL_HIGH_PERF (3), which still disables PSM.
            _wifiLock = wifiMgr.Call<AndroidJavaObject>("createWifiLock", 4, "CoGaze_RealTimeAV");
            _wifiLock.Call("acquire");
            FileLogger.Log("Setup", "[LocalWorkerSetup] WiFi low-latency lock acquired.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LocalWorkerSetup] WiFi lock failed (non-fatal): {ex.Message}");
        }
    }

    private void ReleaseWifiLock()
    {
        try
        {
            if (_wifiLock != null && _wifiLock.Call<bool>("isHeld"))
            {
                _wifiLock.Call("release");
                FileLogger.Log("Setup", "[LocalWorkerSetup] WiFi lock released.");
            }
            _wifiLock?.Dispose();
            _wifiLock = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LocalWorkerSetup] WiFi lock release failed: {ex.Message}");
        }
    }
#endif
}
