using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using System.Collections;
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

    // Kept for reconnect path
    private ExperimentManager  expManager;

    // Video transport
    private UdpVideoTransport  videoTransport;
    private WorkerVideoStream  videoStream;

    public void Initialize()
    {
        // Disable any non-OVR camera so OVRCameraRig takes over
        Camera existingCam = Camera.main;
        if (existingCam != null && existingCam.GetComponentInParent<OVRCameraRig>() == null)
        {
            existingCam.gameObject.SetActive(false);
            Debug.Log("[LocalWorkerSetup] Default Main Camera disabled.");
        }

        OVRCameraRig existingRig = Object.FindAnyObjectByType<OVRCameraRig>();
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
            expManager.Initialize(isExpert: false);

            // WorkerHUD — world-space overlay in the HMD
            var hud = localWorkerInstance.AddComponent<WorkerHUD>();
            hud.Initialize(expManager);

            // ── Video transport (UDP sender) ──────────────────────────
            videoTransport = new UdpVideoTransport();
            videoStream    = localWorkerInstance.AddComponent<WorkerVideoStream>();
            videoStream.Initialize(expManager, videoTransport);

            // Try to connect to Expert immediately if already in room
            TryConnectToExpert();

            Debug.Log("[LocalWorkerSetup] LocalWorker fully initialized.");
        }

        CheckForExistingExpert();
    }

    /// <summary>
    /// Called by SceneBootstrapper on reconnect (instead of Initialize).
    /// Re-registers Photon callbacks and sends a SYNC_REQUEST so the Expert
    /// re-broadcasts the current experiment state — no re-instantiation needed.
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
                return;
            }
        }
        Debug.Log("[LocalWorkerSetup] Expert IP not yet available, will retry on property update.");
    }

    private void OnDestroy()
    {
        videoTransport?.StopSender();
    }
}
