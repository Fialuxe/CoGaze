using System;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// RemoteExpert (PC) side setup.
/// Spawns the RemoteExpert prefab and attaches all handlers.
/// Also attaches ExperimentManager (Expert authority) and ExpertUI.
///
/// On reconnect (called by SceneBootstrapper), skips re-instantiation and
/// re-broadcasts the current experiment state so the Worker can recover.
/// </summary>
public class RemoteExpertSetup : MonoBehaviourPunCallbacks
{
    private const string PREFAB_PATH = "Prefabs/RemoteExpert";
    private const int    VIDEO_PORT  = 9100;
    private const int    AUDIO_PORT  = 9101;

    [Header("Experiment")]
    [Tooltip("Participant number — determines Latin Square condition order (n % 9).")]
    public int participantNumber = 0;

    [Header("Python")]
    [Tooltip("32-bit Python executable — for Tobii/infrared (noise_low). E.g. C:/Python311_32/python.exe")]
    public string pythonExecutable32 = "python";
    [Tooltip("64-bit Python executable — for webcam/high-noise scripts. E.g. C:/Python311/python.exe")]
    public string pythonExecutable64 = "python";
    [Tooltip("Root directory of the EyeTrackToOSCData repository. E.g. C:/Users/mtaku/EyeTrackToOSCData")]
    public string pythonScriptDirectory = "";
    public bool   skipTobiiLaunch       = false;

    [Header("Python Script Args (per block)")]
    [Tooltip("CLI args for Block 0 — Tobii infrared. Usually empty.")]
    public string tobiiScriptArgs     = "";
    [Tooltip("CLI args for Block 1 — Webcam execution script.")]
    public string webcamScriptArgs    = "--weights models/L2CSNet_gaze360.pkl --osc-port 8000";
    [Tooltip("CLI args for Block 2 — High-noise script. Usually empty.")]
    public string highNoiseScriptArgs = "";

    [Header("Python Calibration Args (Webcam only)")]
    [Tooltip("Webcam calibration args (same script as execution). Tobii is calibrated manually.")]
    public string webcamCalibArgs = "--calibrate --weights models/L2CSNet_gaze360.pkl --osc-port 0";

    [Header("Logging")]
    [Tooltip("Root directory for log files. A P{n} subfolder is created inside. Leave empty to use Application.persistentDataPath/logs.")]
    public string logBaseDirectory = "";

    // Set by SceneBootstrapper from AudioDeviceChecker selection
    public string preferredMicDevice = null;

    private GameObject remoteExpertInstance;

    // Kept for reconnect path
    private ExperimentManager expManager;

    // Video transport (kept for cleanup)
    private UdpVideoTransport videoTransport;

    // Audio transport — UDP for low-latency delivery; loss concealed by Opus FEC/PLC
    private UdpAudioTransport audioTransport;

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
            Debug.Log("[RemoteExpertSetup] AudioListener created.");
        }

        remoteExpertInstance = PhotonNetwork.Instantiate(
            PREFAB_PATH, Vector3.zero, Quaternion.identity);
        var view = remoteExpertInstance.GetComponent<PhotonView>();

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

            // ExperimentManager — Expert is the authority
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

            // VoiceCommunicator — two-way audio with spatial playback + WAV recording
            try
            {
                var voice = remoteExpertInstance.AddComponent<VoiceCommunicator>();
                voice.Initialize(true, resolvedLogDir, preferredMicDevice);

                audioTransport = new UdpAudioTransport();
                audioTransport.StartReceiver(AUDIO_PORT);
                voice.SetTransport(audioTransport);

                // Connect to Worker if already in the room when Expert joins.
                // OnPlayerEnteredRoom only fires for players who join AFTER us, so we
                // must explicitly check existing players here.
                foreach (var player in PhotonNetwork.PlayerListOthers)
                    TryConnectAudioToWorker(player);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteExpertSetup] Failed to start VoiceCommunicator: {ex.Message}");
            }

            // ExpertUI — screen-space overlay on the Expert's monitor
            var expertUI = remoteExpertInstance.AddComponent<ExpertUI>();
            expertUI.Initialize(expManager);

            videoTransport = new UdpVideoTransport();
            videoTransport.StartReceiver(VIDEO_PORT);

            // Publish local IP and both port numbers so Worker can reach us directly
            string localIp = UdpVideoTransport.GetLocalIPv4();
            var props = new ExitGames.Client.Photon.Hashtable
            {
                { "ip",        localIp    },
                { "videoPort", VIDEO_PORT },
                { "audioPort", AUDIO_PORT }
            };
            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
            Debug.Log($"[RemoteExpertSetup] Published local IP: {localIp}  video:{VIDEO_PORT}  audio:{AUDIO_PORT}");

            // ExpertVideoDisplay — shows worker's video stream during assembly tasks
            var videoDisplay = remoteExpertInstance.AddComponent<ExpertVideoDisplay>();
            videoDisplay.Initialize(expManager, videoTransport);

            // GazeVisualizer (Expert self-view)
            var vizGo = new GameObject("LocalGazeVisualizer");
            vizGo.AddComponent<GazeVisualizer>().Initialize();

            Debug.Log("[RemoteExpertSetup] RemoteExpert fully initialized.");
        }
    }

    /// <summary>
    /// Called by NetworkManager's reconnect path after re-joining the room.
    /// Re-registers Photon callbacks and re-broadcasts current experiment state
    /// so the rejoining Worker can recover.
    /// </summary>
    public void BroadcastCurrentState()
    {
        if (expManager == null)
        {
            Debug.LogWarning("[RemoteExpertSetup] BroadcastCurrentState: expManager is null.");
            return;
        }

        // Re-register in case callbacks were cleared during disconnect
        PhotonNetwork.AddCallbackTarget(expManager);

        expManager.BroadcastCurrentState();
        Debug.Log("[RemoteExpertSetup] Re-broadcast current experiment state after reconnect.");
    }

    /// <summary>
    /// Called when the Worker joins the room — start the audio sender toward them
    /// if their endpoint is already published.
    /// </summary>
    public override void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
    {
        TryConnectAudioToWorker(newPlayer);
    }

    /// <summary>
    /// Called when any player's custom properties change.
    /// Used to detect when the Worker publishes their IP / audioPort.
    /// </summary>
    public override void OnPlayerPropertiesUpdate(Photon.Realtime.Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps)
    {
        if (changedProps.ContainsKey("ip") || changedProps.ContainsKey("audioPort"))
            TryConnectAudioToWorker(targetPlayer);
    }

    private void TryConnectAudioToWorker(Photon.Realtime.Player player)
    {
        if (audioTransport == null) return;
        if (RoleManager.GetPlayerRole(player) != RoleManager.ROLE_WORKER) return;
        if (!player.CustomProperties.TryGetValue("ip",        out object ipObj)   ||
            !player.CustomProperties.TryGetValue("audioPort", out object portObj)) return;

        string ip   = ipObj.ToString();
        int    port = (int)portObj;
        audioTransport.StartSender(ip, port);
        Debug.Log($"[RemoteExpertSetup] Audio sender → Worker {ip}:{port}");
    }

    private void OnDestroy()
    {
        videoTransport?.StopReceiver();
        audioTransport?.StopSender();
        audioTransport?.StopReceiver();
    }
}
