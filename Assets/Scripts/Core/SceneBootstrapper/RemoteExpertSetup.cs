using System;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using Photon.Voice.Unity;
using Photon.Voice.PUN;
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
    public int participantNumber = 0;

    [Header("Python")]
    public string pythonExecutable32    = "python";
    public string pythonExecutable64    = "python";
    public string pythonScriptDirectory = "";
    public bool   skipTobiiLaunch       = false;

    [Header("Python Script Args (per block)")]
    public string tobiiScriptArgs     = "";
    public string webcamScriptArgs    = "--weights models/L2CSNet_gaze360.pkl --osc-port 8000";
    public string highNoiseScriptArgs = "";

    [Header("Python Calibration Args (Webcam only)")]
    public string webcamCalibArgs = "--calibrate --weights models/L2CSNet_gaze360.pkl --osc-port 0";

    [Header("Logging")]
    public string logBaseDirectory = "";

    public string preferredMicDevice = null;

    private GameObject         remoteExpertInstance;
    private ExperimentManager  expManager;
    private ExpertVideoDisplay videoDisplay;
    private VoiceRecorder      voiceRecorder;

    public void Initialize()
    {
        OVRCameraRig rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
        if (rig != null) { rig.gameObject.SetActive(false); Destroy(rig.gameObject); }

        if (UnityEngine.Object.FindAnyObjectByType<AudioListener>() == null)
        {
            new GameObject("AudioListener").AddComponent<AudioListener>();
            Debug.Log("[RemoteExpertSetup] AudioListener created.");
        }

        remoteExpertInstance = PhotonNetwork.Instantiate(PREFAB_PATH, Vector3.zero, Quaternion.identity);
        var view = remoteExpertInstance.GetComponent<PhotonView>();

        if (view.IsMine)
        {
            // ConnectionHandler
            if (remoteExpertInstance.GetComponent<ConnectionHandler>() == null)
                remoteExpertInstance.AddComponent<ConnectionHandler>();

            // GazeHandler
            var gazeInput   = remoteExpertInstance.AddComponent<OscGazeInput>();
            var gazeHandler = remoteExpertInstance.GetComponent<GazeHandler>();
            if (gazeHandler != null) gazeHandler.Initialize(gazeInput);
            else Debug.LogError("[RemoteExpertSetup] GazeHandler missing.");

            // ExperimentManager
            expManager = remoteExpertInstance.AddComponent<ExperimentManager>();
            expManager.participantNumber     = participantNumber;
            expManager.pythonExecutable32    = pythonExecutable32;
            expManager.pythonExecutable64    = pythonExecutable64;
            expManager.pythonScriptDirectory = pythonScriptDirectory;
            expManager.skipTobiiLaunch       = skipTobiiLaunch;
            expManager.tobiiScriptArgs       = tobiiScriptArgs;
            expManager.webcamScriptArgs      = webcamScriptArgs;
            expManager.highNoiseScriptArgs   = highNoiseScriptArgs;
            expManager.webcamCalibArgs       = webcamCalibArgs;
            expManager.Initialize(isExpert: true);

            string baseDir      = !string.IsNullOrEmpty(logBaseDirectory)
                                  ? logBaseDirectory
                                  : System.IO.Path.Combine(Application.persistentDataPath, "logs");
            string resolvedLog  = System.IO.Path.Combine(baseDir, $"P{participantNumber}");

            // ExperimentLogger
            try
            {
                var logger = remoteExpertInstance.AddComponent<ExperimentLogger>();
                logger.Initialize(expManager, participantNumber, logBaseDirectory);
            }
            catch (Exception ex) { Debug.LogError($"[RemoteExpertSetup] ExperimentLogger: {ex.Message}"); }

            // Photon Voice 2 — set preferred mic on the Recorder already on the prefab
            var recorder = remoteExpertInstance.GetComponentInChildren<Recorder>();
            if (recorder != null && !string.IsNullOrEmpty(preferredMicDevice))
                recorder.MicrophoneDevice = new Photon.Voice.DeviceInfo(preferredMicDevice);
            else if (recorder == null)
                Debug.LogWarning("[RemoteExpertSetup] Recorder not found on RemoteExpert prefab — add PhotonVoiceView + Recorder in the Inspector.");

            // WebRTC DSP — noise suppression + auto-gain on the Expert mic (PC)
            if (recorder != null)
            {
                var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                          ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
                dsp.AEC              = false; // headsets don't need echo cancellation
                dsp.NoiseSuppression = true;
                dsp.AGC              = true;
                dsp.AgcCompressionGain = 30;  // push quiet mics louder (default 9, range 0-90)
                dsp.AgcTargetLevel   = 0;     // target 0 dBFS = maximum loudness
            }

            // VoiceRecorder — WAV recording
            voiceRecorder = remoteExpertInstance.AddComponent<VoiceRecorder>();
            voiceRecorder.Initialize(true, resolvedLog, preferredMicDevice);
            StartCoroutine(WaitForWorkerSpeaker());

            // ExpertUI
            var expertUI = remoteExpertInstance.AddComponent<ExpertUI>();
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

            // GazeVisualizer
            var vizGo = new GameObject("LocalGazeVisualizer");
            vizGo.AddComponent<GazeVisualizer>().Initialize();

            // Signal Worker that signaling is ready — Worker won't call TriggerOffer() until this is set,
            // preventing offer delivery before PhotonNetwork.AddCallbackTarget(this) has run.
            PhotonNetwork.LocalPlayer.SetCustomProperties(
                new ExitGames.Client.Photon.Hashtable { ["expertReady"] = true });

            Debug.Log("[RemoteExpertSetup] RemoteExpert fully initialized.");
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
                Debug.Log("[RemoteExpertSetup] WebRTC offer received from Worker.");
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

    public void BroadcastCurrentState()
    {
        if (expManager == null) return;
        PhotonNetwork.AddCallbackTarget(expManager);
        expManager.BroadcastCurrentState();

        var mesh = remoteExpertInstance?.GetComponent<MeshHandler>();
        mesh?.SendMeshTransform();

        Debug.Log("[RemoteExpertSetup] Re-broadcast state after reconnect.");
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

    private System.Collections.IEnumerator WaitForWorkerSpeaker()
    {
        // Cancel any previous search before starting a new one.
        if (_speakerSearchCoroutine != null)
            StopCoroutine(_speakerSearchCoroutine);
        _speakerSearchCoroutine = null;

        // Poll until PunVoiceClient links the remote stream to the Speaker.
        // No hard timeout — the Worker may join long after the Expert does.
        while (true)
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
        }
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
    }
}
