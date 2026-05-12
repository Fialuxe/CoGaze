using System;
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using Photon.Voice.Unity;
using Photon.Voice.PUN;
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
    private ExperimentManager  expManager;
    private WorkerVideoStream  videoStream;
    private VoiceRecorder      voiceRecorder;
    private bool               _offerTriggered;
    private bool               _expertAudioAttached;

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _wifiLock;
#endif

    public void Initialize()
    {
        Camera existingCam = Camera.main;
        if (existingCam != null && existingCam.GetComponentInParent<OVRCameraRig>() == null)
        {
            existingCam.gameObject.SetActive(false);
            Debug.Log("[LocalWorkerSetup] Default camera disabled.");
        }

        if (UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>() == null)
            Debug.LogWarning("[LocalWorkerSetup] OVRCameraRig not found.");

        localWorkerInstance = PhotonNetwork.Instantiate(PREFAB_PATH, Vector3.zero, Quaternion.identity);
        localWorkerView = localWorkerInstance.GetComponent<PhotonView>();

        if (localWorkerView.IsMine)
        {
            // PostureHandler
            var postureInput   = localWorkerInstance.AddComponent<MetaXRPostureInput>();
            var postureHandler = localWorkerInstance.GetComponent<PostureHandler>();
            if (postureHandler != null) postureHandler.Initialize(postureInput);
            else Debug.LogError("[LocalWorkerSetup] PostureHandler missing.");

            // GazeHandler
            var gazeInput   = localWorkerInstance.AddComponent<MetaXRGazeInput>();
            var gazeHandler = localWorkerInstance.GetComponent<GazeHandler>();
            if (gazeHandler != null) gazeHandler.Initialize(gazeInput);
            else Debug.LogError("[LocalWorkerSetup] GazeHandler missing.");

            foreach (var r in localWorkerInstance.GetComponentsInChildren<MeshRenderer>(true))
                r.enabled = false;

            // ExperimentManager
            expManager = localWorkerInstance.AddComponent<ExperimentManager>();
            expManager.participantNumber = participantNumber;
            expManager.Initialize(isExpert: false);

#if UNITY_ANDROID && !UNITY_EDITOR
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

            // WorkerHUD
            var hud = localWorkerInstance.AddComponent<WorkerHUD>();
            hud.Initialize(expManager);

            // Photon Voice 2 — set preferred mic on the Recorder already on the prefab
            var recorder = localWorkerInstance.GetComponentInChildren<Recorder>();
            if (recorder != null && !string.IsNullOrEmpty(preferredMicDevice))
                recorder.MicrophoneDevice = new Photon.Voice.DeviceInfo(preferredMicDevice);
            else if (recorder == null)
                Debug.LogWarning("[LocalWorkerSetup] Recorder not found on LocalWorker prefab — add PhotonVoiceView + Recorder in the Inspector.");

            // Use Photon mic type so Android hardware AEC/AGC/NS actually activate
            if (recorder != null)
            {
                recorder.MicrophoneType = Recorder.MicType.Photon;
                recorder.SetAndroidNativeMicrophoneSettings(aec: true, agc: true, ns: true);

                // Software DSP on top: aggressive AGC to push quiet Quest mic louder,
                // plus NS to cut through white noise on the Expert's end.
                var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                          ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
                dsp.AEC              = false;
                dsp.NoiseSuppression = true;
                dsp.AGC              = true;
                dsp.AgcCompressionGain = 60;
                dsp.AgcTargetLevel   = 0;
            }

            // VoiceRecorder — WAV recording independent of PV2
            string logDir = System.IO.Path.Combine(
                Application.persistentDataPath, "logs", $"P{participantNumber}");
            voiceRecorder = localWorkerInstance.AddComponent<VoiceRecorder>();
            voiceRecorder.Initialize(false, logDir, preferredMicDevice);

            // VideoStream — no transport arg; WebRTC signaling wired below
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

            Debug.Log("[LocalWorkerSetup] LocalWorker fully initialized.");

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

    public void RequestStateSync()
    {
        if (expManager == null) return;
        PhotonNetwork.AddCallbackTarget(expManager);
        expManager.SendSyncRequest();
        Debug.Log("[LocalWorkerSetup] RequestStateSync sent.");
    }

    private static bool IsExpertReady(Player player) =>
        player.CustomProperties.TryGetValue("expertReady", out var v) && v is bool b && b;

    private void TriggerOfferOnce()
    {
        if (_offerTriggered) return;
        _offerTriggered = true;
        Debug.Log("[LocalWorkerSetup] Triggering WebRTC offer (once).");
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
                    Debug.Log("[LocalWorkerSetup] Expert already ready — triggering offer.");
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
            Debug.Log("[LocalWorkerSetup] Expert signaled ready — triggering offer.");
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
                    Debug.Log("[LocalWorkerSetup] Expert joined and is ready — triggering offer.");
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

    // Wait until PunVoiceClient links the remote Expert's Speaker, then attach capture.
    private void TryAttachRemoteCaptureToExpert()
    {
        if (voiceRecorder == null) return;
        StartCoroutine(WaitForExpertSpeaker());
    }

    private IEnumerator WaitForExpertSpeaker()
    {
        while (true)
        {
            foreach (var pvv in FindObjectsByType<PhotonVoiceView>(FindObjectsSortMode.None))
            {
                if (pvv.GetComponent<PhotonView>()?.IsMine == false && pvv.SpeakerInUse != null)
                {
                    var src = pvv.SpeakerInUse.GetComponent<AudioSource>();
                    if (src != null)
                    {
                        // Let PhotonTransformView control position naturally.
                        // Just configure rolloff so audio is audible at typical distances.
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
        }
    }

    private void SpawnGazeVisualizer()
    {
        if (gazeVisualizerInstance != null) return;
        gazeVisualizerInstance = new GameObject("LocalGazeVisualizer");
        gazeVisualizerInstance.AddComponent<GazeVisualizer>().Initialize();
        Debug.Log("[LocalWorkerSetup] GazeVisualizer spawned.");
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
            _wifiLock = wifiMgr.Call<AndroidJavaObject>("createWifiLock", 4, "CoGaze_RealTimeAV");
            _wifiLock.Call("acquire");
            Debug.Log("[LocalWorkerSetup] WiFi low-latency lock acquired.");
        }
        catch (Exception ex) { Debug.LogWarning($"[LocalWorkerSetup] WiFi lock failed: {ex.Message}"); }
    }

    private void ReleaseWifiLock()
    {
        try
        {
            if (_wifiLock != null && _wifiLock.Call<bool>("isHeld"))
                _wifiLock.Call("release");
            _wifiLock?.Dispose();
            _wifiLock = null;
        }
        catch (Exception ex) { Debug.LogWarning($"[LocalWorkerSetup] WiFi lock release failed: {ex.Message}"); }
    }
#endif
}
