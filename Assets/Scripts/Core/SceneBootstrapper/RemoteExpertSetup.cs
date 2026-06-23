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

/// <summary>
/// RemoteExpert (PC) setup.
/// - Instantiates RemoteExpert prefab and attaches all handlers.
/// - Configures Photon Voice 2 Recorder with the selected mic device.
/// - Wires WebRTC signaling (via Photon RaiseEvent) between ExpertVideoDisplay and Worker.
/// - Attaches VoiceRecorder for WAV recording.
/// </summary>
public class RemoteExpertSetup : MonoBehaviourPunCallbacks, IOnEventCallback
{
    private const string PREFAB_PATH = "Prefabs/RemoteExpert";

    [Header("Experiment")]
    [Tooltip("Participant number — determines Latin Square condition order (n % 9).")]
    public int participantNumber = 0;

    [Header("Logging")]
    [Tooltip("Root directory for log files. A P{n} subfolder is created inside. Leave empty to use Application.persistentDataPath/logs.")]
    public string logBaseDirectory = "";

    public string preferredMicDevice = null;

    private GameObject         remoteExpertInstance;
    private ExperimentManager2 expManager;
    private ExpertVideoDisplay videoDisplay;
    private VoiceRecorder      voiceRecorder;

    public void Initialize()
    {
        OVRCameraRig existingRig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
        if (existingRig != null)
        {
            existingRig.gameObject.SetActive(false);
            Destroy(existingRig.gameObject);
        }

        // OVRCameraRig carries the scene's only AudioListener — restore it immediately
        // so white noise and any other audio works from the first frame.
        if (UnityEngine.Object.FindAnyObjectByType<AudioListener>() == null)
        {
            var listenerGo = new GameObject("AudioListener");
            listenerGo.AddComponent<AudioListener>();
            FileLogger.Log("Setup", "[RemoteExpertSetup] AudioListener created.");
        }

        remoteExpertInstance = PhotonNetwork.Instantiate(PREFAB_PATH, Vector3.zero, Quaternion.identity);
        if (remoteExpertInstance == null)
        {
            Debug.LogError("[RemoteExpertSetup] PhotonNetwork.Instantiate returned null for RemoteExpert prefab.");
            return;
        }
        var view = remoteExpertInstance.GetComponent<PhotonView>();
        if (view == null)
        {
            Debug.LogError("[RemoteExpertSetup] PhotonView missing on instantiated RemoteExpert prefab.");
            return;
        }

        if (view.IsMine)
        {
            // ConnectionHandler (FPS camera + transform sync)
            if (remoteExpertInstance.GetComponent<ConnectionHandler>() == null)
                remoteExpertInstance.AddComponent<ConnectionHandler>();

            // PostureHandler
            var postureHandler = remoteExpertInstance.GetComponent<PostureHandler>();
            if (postureHandler == null)
                Debug.LogError("[RemoteExpertSetup] PostureHandler missing from RemoteExpert prefab.");

            // GazeHandler + OscGazeInput
            var gazeInput   = remoteExpertInstance.AddComponent<OscGazeInput>();
            var gazeHandler = remoteExpertInstance.GetComponent<GazeHandler>();
            if (gazeHandler != null) gazeHandler.Initialize(gazeInput);
            else Debug.LogError("[RemoteExpertSetup] GazeHandler missing from RemoteExpert prefab.");

            // MeshHandler
            if (remoteExpertInstance.GetComponent<MeshHandler>() == null)
                Debug.LogError("[RemoteExpertSetup] MeshHandler missing from RemoteExpert prefab.");

            // ExperimentManager2 — Expert is the authority
            expManager = remoteExpertInstance.AddComponent<ExperimentManager2>();
            expManager.participantNumber = participantNumber;
            expManager.Initialize(isExpert: true);

            string baseDir = !string.IsNullOrEmpty(logBaseDirectory)
                ? logBaseDirectory
                : System.IO.Path.Combine(Application.persistentDataPath, "logs");
            string resolvedLogDir = System.IO.Path.Combine(baseDir, $"P{participantNumber}");

            // ExperimentLogger — trial CSV + frame CSV + replay JSON
            try
            {
                var logger = remoteExpertInstance.AddComponent<ExperimentLogger>();
                logger.Initialize(expManager, participantNumber, logBaseDirectory);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteExpertSetup] Failed to start ExperimentLogger: {ex.Message}");
            }

            // Photon Voice 2 — set preferred mic on the Recorder already on the prefab
            var recorder = remoteExpertInstance.GetComponentInChildren<Recorder>();
            if (recorder != null && !string.IsNullOrEmpty(preferredMicDevice))
                recorder.MicrophoneDevice = new Photon.Voice.DeviceInfo(preferredMicDevice);
            else if (recorder == null)
                Debug.LogWarning("[RemoteExpertSetup] Recorder not found on RemoteExpert prefab — add PhotonVoiceView + Recorder in the Inspector.");

            // SW DSP on the Expert (PC) — no hardware AEC fallback so keep NS+AGC,
            // but with sane values: AgcTargetLevel=3 leaves headroom; gain=18 is 2x
            // the class default (9) without the clipping risk of 30 or 60.
            // AEC=false is correct if headphones are used (required by experiment protocol).
            if (recorder != null)
            {
                var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                          ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
                dsp.AEC              = false;
                dsp.NoiseSuppression = true;
                dsp.AGC              = true;
                dsp.AgcCompressionGain = 18;
                dsp.AgcTargetLevel   = 3;

                recorder.SamplingRate  = SamplingRate.Sampling48000; // PC mic does not support 16000; use 48000
                recorder.FrameDuration = OpusCodec.FrameDuration.Frame20ms;
                recorder.Bitrate       = 24000;
            }

            // VoiceRecorder — WAV recording independent of PV2
            voiceRecorder = remoteExpertInstance.AddComponent<VoiceRecorder>();
            voiceRecorder.Initialize(true, resolvedLogDir, preferredMicDevice);
            StartCoroutine(WaitForWorkerSpeaker());

            // ExpertUI2 — screen-space overlay on the Expert's monitor
            var expertUI = remoteExpertInstance.AddComponent<ExpertUI2>();
            expertUI.Initialize(expManager);

            // ExpertVideoDisplay — WebRTC answerer; signaling wired below
            PhotonNetwork.AddCallbackTarget(this);
            videoDisplay = remoteExpertInstance.AddComponent<ExpertVideoDisplay>();
            videoDisplay.Initialize(expManager);

            var s = videoDisplay.Session;
            s.OnSendOffer  += sdp => RaiseSignal(WebRtcVideoSession.EVT_OFFER,  new[] { sdp });
            s.OnSendAnswer += sdp => RaiseSignal(WebRtcVideoSession.EVT_ANSWER, new[] { sdp });
            s.OnSendIce    += (c, mid, idx) =>
                RaiseSignal(WebRtcVideoSession.EVT_ICE, new[] { c, mid, idx.ToString() });

            // GazeVisualizer (Expert self-view)
            var vizGo = new GameObject("LocalGazeVisualizer");
            vizGo.AddComponent<GazeVisualizer>().Initialize();

            // Signal Worker that signaling is ready — Worker won't call TriggerOffer() until this is set,
            // preventing offer delivery before PhotonNetwork.AddCallbackTarget(this) has run.
            Debug.Log("[RemoteExpertSetup] Setting expertReady=true — Worker can now send WebRTC offer.");
            PhotonNetwork.LocalPlayer.SetCustomProperties(
                new ExitGames.Client.Photon.Hashtable { ["expertReady"] = true });

            FileLogger.Log("Setup", "[RemoteExpertSetup] RemoteExpert fully initialized.");
        }
    }

    // ── WebRTC signaling helpers ─────────────────────────────────────────────

    private static void RaiseSignal(byte evtCode, string[] data)
    {
        PhotonNetwork.RaiseEvent(evtCode, data,
            new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
    }

    // IOnEventCallback — receives signaling from Worker
    public void OnEvent(EventData ev)
    {
        var session = videoDisplay?.Session;
        if (session == null)
        {
            Debug.LogWarning($"[RemoteExpertSetup] OnEvent code={ev.Code} received but session is null (videoDisplay={videoDisplay != null})");
            return;
        }

        switch (ev.Code)
        {
            case WebRtcVideoSession.EVT_OFFER:
                Debug.Log("[RemoteExpertSetup] WebRTC offer received from Worker — sending answer.");
                FileLogger.Log("Setup", "[RemoteExpertSetup] WebRTC offer received from Worker.");
                session.ApplyRemoteOffer(((string[])ev.CustomData)[0]);
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

    // ── Reconnect path ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by NetworkManager's reconnect path after re-joining the room.
    /// Re-registers Photon callbacks and re-broadcasts current experiment state.
    /// </summary>
    public void BroadcastCurrentState()
    {
        if (expManager == null)
        {
            Debug.LogWarning("[RemoteExpertSetup] BroadcastCurrentState: expManager is null.");
            return;
        }
        PhotonNetwork.AddCallbackTarget(expManager);
        expManager.BroadcastCurrentState();

        var mesh = remoteExpertInstance?.GetComponent<MeshHandler>();
        mesh?.SendMeshTransform();

        FileLogger.Log("Setup", "[RemoteExpertSetup] Re-broadcast current experiment state after reconnect.");
    }

    // ── Remote capture for WAV ────────────────────────────────────────────────

    // Called when any player joins — restart the Speaker search so we catch
    // a Worker who joins after Initialize() has already run.
    public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
    {
        if (RoleManager.GetPlayerRole(newPlayer) == RoleManager.ROLE_WORKER)
            StartCoroutine(WaitForWorkerSpeaker());
    }

    private Coroutine _speakerSearchCoroutine;

    private IEnumerator WaitForWorkerSpeaker()
    {
        if (_speakerSearchCoroutine != null)
            StopCoroutine(_speakerSearchCoroutine);
        _speakerSearchCoroutine = null;

        // Bounded wait — see SceneBootstrapper2.WaitForRemoteSpeaker: without a timeout this
        // spins forever if the Worker never publishes a Speaker, hiding the real failure.
        float elapsed = 0f;
        const float timeout = 30f;
        while (elapsed < timeout)
        {
            foreach (var pvv in FindObjectsByType<PhotonVoiceView>(FindObjectsSortMode.None))
            {
                if (pvv.GetComponent<PhotonView>()?.IsMine == false && pvv.SpeakerInUse != null)
                {
                    var src = pvv.SpeakerInUse.GetComponent<AudioSource>();
                    if (src != null) { src.volume = 3f; src.spatialBlend = 0f; }
                    voiceRecorder?.AttachRemoteCapture(pvv.SpeakerInUse);
                    _speakerSearchCoroutine = null;
                    yield break;
                }
            }
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        _speakerSearchCoroutine = null;
        Debug.LogWarning($"[RemoteExpertSetup] Worker Speaker not found within {timeout:F0}s — remote audio capture not started.");
        FileLogger.Log("Setup", "[RemoteExpertSetup] WaitForWorkerSpeaker timed out.");
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
    }
}
