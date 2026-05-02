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
public class RemoteExpertSetup : MonoBehaviour
{
    private const string PREFAB_PATH = "Prefabs/RemoteExpert";
    private const int    VIDEO_PORT  = 9100;

    private GameObject remoteExpertInstance;

    // Kept for reconnect path
    private ExperimentManager expManager;

    // Video transport (kept for cleanup)
    private UdpVideoTransport videoTransport;

    public void Initialize()
    {
        OVRCameraRig existingRig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (existingRig != null)
        {
            existingRig.gameObject.SetActive(false);
            Destroy(existingRig.gameObject);
        }

        // OVRCameraRig carries the scene's only AudioListener — restore it immediately
        // so white noise and any other audio works from the first frame.
        if (Object.FindAnyObjectByType<AudioListener>() == null)
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
            expManager.Initialize(isExpert: true);

            // ExpertUI — screen-space overlay on the Expert's monitor
            var expertUI = remoteExpertInstance.AddComponent<ExpertUI>();
            expertUI.Initialize(expManager);

            // ── Video transport (UDP receiver) ──────────────────────────
            videoTransport = new UdpVideoTransport();
            videoTransport.StartReceiver(VIDEO_PORT);

            // Publish local IP so Worker can find us
            string localIp = UdpVideoTransport.GetLocalIPv4();
            var props = new ExitGames.Client.Photon.Hashtable
            {
                { "ip", localIp },
                { "videoPort", VIDEO_PORT }
            };
            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
            Debug.Log($"[RemoteExpertSetup] Published local IP: {localIp}:{VIDEO_PORT}");

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
    /// Called by SceneBootstrapper on reconnect (instead of Initialize).
    /// Re-registers Photon callbacks and re-broadcasts the current experiment
    /// state so any reconnecting Worker receives it without a full scene reset.
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

    private void OnDestroy()
    {
        videoTransport?.StopReceiver();
    }
}
