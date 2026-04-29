using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using System.Collections;

/// <summary>
/// LocalWorker (Android/MetaXR HMD) 側のセットアップ。
/// LocalWorker PrefabをInstantiateし、PostureHandler・GazeHandler・MeshHandlerをAddComponentする。
/// Expert参加時にGazeVisualizer Prefabを生成する。
/// </summary>
public class LocalWorkerSetup : MonoBehaviourPunCallbacks
{
    private const string PREFAB_PATH = "Prefabs/LocalWorker";
    private const string GAZE_VISUALIZER_PREFAB = "Prefabs/GazeVisualizer";
    private const string OVR_RIG_PREFAB = "Prefabs/OVRCameraRigSetup";

    private GameObject localWorkerInstance;
    private PhotonView localWorkerView;
    private GameObject gazeVisualizerInstance;
    private GameObject ovrRigInstance;

    public void Initialize()
    {
        // OVRCameraRigに属していない「純粋なUnityの初期カメラ」があれば無効化する
        // （OVRCameraRigのCenterEyeAnchorまで無効化してしまうとトラッキングが壊れるため）
        Camera existingMainCam = Camera.main;
        if (existingMainCam != null && existingMainCam.GetComponentInParent<OVRCameraRig>() == null)
        {
            existingMainCam.gameObject.SetActive(false);
            Debug.Log("[LocalWorkerSetup] Default Main Camera disabled to use OVRCameraRig instead.");
        }

        // 事前配置されたOVRCameraRigを探す（動的生成だとMeta XRのトラッキングが壊れるため）
        OVRCameraRig existingRig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (existingRig != null)
        {
            ovrRigInstance = existingRig.gameObject;
            Debug.Log("[LocalWorkerSetup] Found pre-placed OVRCameraRig in the scene.");
        }
        else
        {
            Debug.LogWarning("[LocalWorkerSetup] OVRCameraRig not found in the scene! " +
                "Please drag Resources/Prefabs/OVRCameraRigSetup into the scene directly. HMD tracking may not work.");
        }

        localWorkerInstance = PhotonNetwork.Instantiate(
            PREFAB_PATH, Vector3.zero, Quaternion.identity);
        localWorkerView = localWorkerInstance.GetComponent<PhotonView>();

        if (localWorkerView.IsMine)
        {
            // PostureHandler + MetaXRPostureInput
            var postureInput = localWorkerInstance.AddComponent<MetaXRPostureInput>();
            var postureHandler = localWorkerInstance.GetComponent<PostureHandler>();
            if (postureHandler != null) postureHandler.Initialize(postureInput);
            else Debug.LogError("[LocalWorkerSetup] PostureHandler is missing from LocalWorker Prefab!");

            // GazeHandler + MetaXRGazeInput
            var gazeInput = localWorkerInstance.AddComponent<MetaXRGazeInput>();
            var gazeHandler = localWorkerInstance.GetComponent<GazeHandler>();
            if (gazeHandler != null) gazeHandler.Initialize(gazeInput);
            else Debug.LogError("[LocalWorkerSetup] GazeHandler is missing from LocalWorker Prefab!");

            // MeshHandler
            if (localWorkerInstance.GetComponent<MeshHandler>() == null)
                Debug.LogError("[LocalWorkerSetup] MeshHandler is missing from LocalWorker Prefab!");

            // 自分のアバターがVRの視界を遮らないように非表示にする
            foreach (var renderer in localWorkerInstance.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.enabled = false;
            }

            Debug.Log("[LocalWorkerSetup] LocalWorker initialized with all handlers.");
        }

        // Expert がすでにルームにいるか確認
        CheckForExistingExpert();
    }

    /// <summary>既にルームにいるExpertを探してGazeVisualizerを生成</summary>
    private void CheckForExistingExpert()
    {
        foreach (var player in PhotonNetwork.PlayerListOthers)
        {
            string role = RoleManager.GetPlayerRole(player);
            if (role == RoleManager.ROLE_EXPERT)
            {
                SpawnGazeVisualizer();
                return;
            }
        }
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        // プロパティの同期を待ってからロールを確認
        StartCoroutine(WaitForRoleAndSpawn(newPlayer));
    }

    private IEnumerator WaitForRoleAndSpawn(Player player)
    {
        float timeout = 5f;
        float elapsed = 0f;
        while (elapsed < timeout)
        {
            string role = RoleManager.GetPlayerRole(player);
            if (role == RoleManager.ROLE_EXPERT)
            {
                SpawnGazeVisualizer();
                yield break;
            }
            elapsed += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }
        Debug.LogWarning($"[LocalWorkerSetup] Timed out waiting for role of {player.NickName}");
    }

    private void SpawnGazeVisualizer()
    {
        if (gazeVisualizerInstance != null) return;

        gazeVisualizerInstance = new GameObject("LocalGazeVisualizer");
        var gazeVis = gazeVisualizerInstance.AddComponent<GazeVisualizer>();
        gazeVis.Initialize();

        Debug.Log("[LocalWorkerSetup] GazeVisualizer spawned locally.");
    }
}
