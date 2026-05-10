using System;
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

/// <summary>
/// LocalWorker (Android / Meta Quest) side setup.
/// Spawns the LocalWorker prefab, attaches all handlers,
/// attaches ExperimentManager (Worker mirror) and WorkerHUD.
///
/// On reconnect (called by SceneBootstrapper), skips re-instantiation and
/// instead sends a SYNC_REQUEST so the Expert re-broadcasts current state.
/// </summary>
public class LocalWorkerSetup : MonoBehaviourPunCallbacks
{
    private const string PREFAB_PATH           = "Prefabs/LocalWorker";
    private const string GAZE_VISUALIZER_PREFAB = "Prefabs/GazeVisualizer";

    private GameObject         localWorkerInstance;
    private PhotonView         localWorkerView;
    private GameObject         gazeVisualizerInstance;

    // Set by SceneBootstrapper before Initialize()
    public int    participantNumber  = 0;
    public string preferredMicDevice = null;

    // Kept for reconnect path
    private ExperimentManager  expManager;

    // Video transport
    private UdpVideoTransport  videoTransport;
    private WorkerVideoStream  videoStream;

    // Audio transport — UDP for low-latency delivery; loss concealed by Opus FEC/PLC
    private const int          AUDIO_PORT = 9102;
    private UdpAudioTransport  audioTransport;

#if UNITY_ANDROID && !UNITY_EDITOR
    // WIFI_MODE_FULL_LOW_LATENCY (4, Android 10+) disables the WiFi PSM (power-saving mode).
    // Without this lock the AP can batch-deliver packets at the DTIM interval (up to 500 ms),
    // which is the single largest source of audio dropout on Quest even on a strong signal.
    private AndroidJavaObject _wifiLock;
#endif

    public void Initialize()
    {
        // Disable any non-OVR camera so OVRCameraRig takes over
        Camera existingCam = Camera.main;
        if (existingCam != null && existingCam.GetComponentInParent<OVRCameraRig>() == null)
        {
            existingCam.gameObject.SetActive(false);
            Debug.Log("[LocalWorkerSetup] Default Main Camera disabled.");
        }

        OVRCameraRig existingRig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
        if (existingRig == null)
            Debug.LogWarning("[LocalWorkerSetup] OVRCameraRig not found. Place OVRCameraRigSetup prefab in scene.");

        localWorkerInstance = PhotonNetwork.Instantiate(
            PREFAB_PATH, Vector3.zero, Quaternion.identity);
        localWorkerView = localWorkerInstance.GetComponent<PhotonView>();

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

            // ExperimentManager — Worker is a mirror receiver
            expManager = localWorkerInstance.AddComponent<ExperimentManager>();
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
                Debug.LogError($"[LocalWorkerSetup] Failed to start WorkerHandBroadcaster: {ex.Message}");
            }
#endif

            // VoiceCommunicator — two-way audio with spatial playback + WAV recording
            try
            {
                string logDir = System.IO.Path.Combine(
                    Application.persistentDataPath, "logs", $"P{participantNumber}");
                var voice = localWorkerInstance.AddComponent<VoiceCommunicator>();
                voice.Initialize(false, logDir, preferredMicDevice);

                audioTransport = new UdpAudioTransport();
                audioTransport.StartReceiver(AUDIO_PORT);
                voice.SetTransport(audioTransport);

                // Publish Worker's audio endpoint so Expert can start sending back
                string workerIp = UdpVideoTransport.GetLocalIPv4();
                var audioProps = new ExitGames.Client.Photon.Hashtable
                {
                    { "ip",        workerIp   },
                    { "audioPort", AUDIO_PORT }
                };
                PhotonNetwork.LocalPlayer.SetCustomProperties(audioProps);
                Debug.Log($"[LocalWorkerSetup] Published audio endpoint: {workerIp}:{AUDIO_PORT}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LocalWorkerSetup] Failed to start VoiceCommunicator: {ex.Message}");
            }

            // WorkerHUD — world-space overlay in the HMD
            var hud = localWorkerInstance.AddComponent<WorkerHUD>();
            hud.Initialize(expManager);

            videoTransport = new UdpVideoTransport();
            videoStream    = localWorkerInstance.AddComponent<WorkerVideoStream>();
            videoStream.Initialize(expManager, videoTransport);

            // Try to connect to Expert immediately if already in room
            TryConnectToExpert();

            Debug.Log("[LocalWorkerSetup] LocalWorker fully initialized.");

#if UNITY_ANDROID && !UNITY_EDITOR
            AcquireWifiLock();
#endif
        }

        CheckForExistingExpert();
    }

    /// <summary>
    /// Called by SceneBootstrapper after re-joining the room following a disconnect.
    /// Re-registers Photon callbacks and sends a SYNC_REQUEST so the Expert
    /// re-broadcasts current state.
    /// </summary>
    public void RequestStateSync()
    {
        if (expManager == null)
        {
            Debug.LogWarning("[LocalWorkerSetup] RequestStateSync: expManager is null. Was Initialize called?");
            return;
        }

        // Re-register callback target in case it was cleared during disconnect
        PhotonNetwork.AddCallbackTarget(expManager);

        // Ask Expert to resend state (includes RemainingSeconds for timer recovery)
        expManager.SendSyncRequest();
        Debug.Log("[LocalWorkerSetup] RequestStateSync sent.");
    }

    private void CheckForExistingExpert()
    {
        foreach (var player in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(player) == RoleManager.ROLE_EXPERT)
            {
                SpawnGazeVisualizer();
                return;
            }
        }
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        StartCoroutine(WaitForRoleAndSpawn(newPlayer));
    }

    /// <summary>
    /// Called when any player's custom properties change.
    /// Used to detect when the Expert publishes their IP address.
    /// </summary>
    public override void OnPlayerPropertiesUpdate(Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps)
    {
        if (changedProps.ContainsKey("ip"))
        {
            TryConnectToExpert();
        }
    }

    private IEnumerator WaitForRoleAndSpawn(Player player)
    {
        float timeout = 5f, elapsed = 0f;
        while (elapsed < timeout)
        {
            if (RoleManager.GetPlayerRole(player) == RoleManager.ROLE_EXPERT)
            {
                SpawnGazeVisualizer();
                TryConnectToExpert();
                yield break;
            }
            elapsed += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }
        Debug.LogWarning($"[LocalWorkerSetup] Role timeout for {player.NickName}.");
    }

    private void SpawnGazeVisualizer()
    {
        if (gazeVisualizerInstance != null) return;
        gazeVisualizerInstance = new GameObject("LocalGazeVisualizer");
        gazeVisualizerInstance.AddComponent<GazeVisualizer>().Initialize();
        Debug.Log("[LocalWorkerSetup] GazeVisualizer spawned.");
    }

    /// <summary>
    /// Find Expert's IP from Photon custom properties and start UDP sender.
    /// </summary>
    private void TryConnectToExpert()
    {
        if (videoTransport == null) return;

        foreach (var player in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(player) != RoleManager.ROLE_EXPERT) continue;

            if (player.CustomProperties.TryGetValue("ip", out object ipObj) &&
                player.CustomProperties.TryGetValue("videoPort", out object portObj))
            {
                string ip = ipObj.ToString();
                int port  = (int)portObj;
                videoTransport.StartSender(ip, port);
                Debug.Log($"[LocalWorkerSetup] Video sender connected to Expert at {ip}:{port}");

                // Start audio sender toward Expert if their audioPort is published
                if (audioTransport != null &&
                    player.CustomProperties.TryGetValue("audioPort", out object audioPortObj))
                {
                    int audioPort = (int)audioPortObj;
                    audioTransport.StartSender(ip, audioPort);
                    Debug.Log($"[LocalWorkerSetup] Audio sender → Expert {ip}:{audioPort}");
                }
                return;
            }
        }
        Debug.Log("[LocalWorkerSetup] Expert IP not yet available, will retry on property update.");
    }

    private void OnDestroy()
    {
        videoTransport?.StopSender();
        audioTransport?.StopSender();
        audioTransport?.StopReceiver();
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
            Debug.Log("[LocalWorkerSetup] WiFi low-latency lock acquired.");
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
                Debug.Log("[LocalWorkerSetup] WiFi lock released.");
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
