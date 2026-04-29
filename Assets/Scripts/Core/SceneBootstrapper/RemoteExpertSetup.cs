using UnityEngine;
using Photon.Pun;

/// <summary>
/// RemoteExpert (PC) 側のセットアップ。
/// RemoteExpert PrefabをInstantiateし、ConnectionHandler・GazeHandler・MeshHandlerをAddComponentする。
/// GazeHandlerにはOscGazeInputを注入し、外部アイトラッカーからOSC経由で受信した視線データを送信する。
/// </summary>
public class RemoteExpertSetup : MonoBehaviour
{
    private const string PREFAB_PATH = "Prefabs/RemoteExpert";

    private GameObject remoteExpertInstance;

    public void Initialize()
    {
        // Expert側（PC）ではVR用のOVRCameraRigは不要なので、シーンにあれば削除して干渉を防ぐ
        OVRCameraRig existingRig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (existingRig != null)
        {
            // Destroyはフレームの最後に行われるため、先にSetActive(false)してCamera.mainから外す
            existingRig.gameObject.SetActive(false);
            Destroy(existingRig.gameObject);
            Debug.Log("[RemoteExpertSetup] Disabled and Destroyed OVRCameraRig (Not needed for Expert).");
        }

        remoteExpertInstance = PhotonNetwork.Instantiate(
            PREFAB_PATH, Vector3.zero, Quaternion.identity);
        var view = remoteExpertInstance.GetComponent<PhotonView>();

        if (view.IsMine)
        {
            // ConnectionHandler（FPSカメラ操作 + transform同期）
            if (remoteExpertInstance.GetComponent<ConnectionHandler>() == null)
                remoteExpertInstance.AddComponent<ConnectionHandler>();

            // PostureHandler（Expertのカメラ位置をWorker側に同期）
            var postureHandler = remoteExpertInstance.GetComponent<PostureHandler>();
            if (postureHandler == null) Debug.LogError("[RemoteExpertSetup] PostureHandler is missing from RemoteExpert Prefab!");

            // GazeHandler + OscGazeInput（extOSC経由で視線データ受信）
            var gazeInput = remoteExpertInstance.AddComponent<OscGazeInput>();
            var gazeHandler = remoteExpertInstance.GetComponent<GazeHandler>();
            if (gazeHandler != null) gazeHandler.Initialize(gazeInput);
            else Debug.LogError("[RemoteExpertSetup] GazeHandler is missing from RemoteExpert Prefab!");

            // MeshHandler
            if (remoteExpertInstance.GetComponent<MeshHandler>() == null)
                Debug.LogError("[RemoteExpertSetup] MeshHandler is missing from RemoteExpert Prefab!");

            // GazeVisualizerをローカル生成（自分がどこを見ているか確認するため）
            var gazeVisualizerInstance = new GameObject("LocalGazeVisualizer");
            var gazeVis = gazeVisualizerInstance.AddComponent<GazeVisualizer>();
            gazeVis.Initialize();

            Debug.Log("[RemoteExpertSetup] RemoteExpert initialized with all handlers.");
        }
    }
}
