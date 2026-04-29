using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using System;

/// <summary>
/// Photon接続とルーム参加を管理するシングルトン。
/// SceneBootstrapperがAwake()で生成し、DontDestroyOnLoadをかける。
/// </summary>
public class NetworkManager : MonoBehaviourPunCallbacks
{
    public static NetworkManager Instance { get; private set; }

    /// <summary>ルーム参加完了時に発火するイベント</summary>
    public event Action OnRoomJoined;

    private const string ROOM_NAME = "CoGaze_Room";
    private const string FIXED_REGION = "asia";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>Photonサーバーへ接続を開始する</summary>
    public void Connect()
    {
        if (PhotonNetwork.IsConnected)
        {
            Debug.Log("[NetworkManager] Already connected.");
            return;
        }

        PhotonNetwork.PhotonServerSettings.AppSettings.FixedRegion = FIXED_REGION;
        PhotonNetwork.ConnectUsingSettings();
        Debug.Log("[NetworkManager] Connecting to Photon (region: asia)...");
    }

    public override void OnConnectedToMaster()
    {
        Debug.Log("[NetworkManager] Connected to Master. Joining room...");
        RoomOptions options = new RoomOptions
        {
            MaxPlayers = 4,
            IsVisible = true,
            IsOpen = true
        };
        PhotonNetwork.JoinOrCreateRoom(ROOM_NAME, options, TypedLobby.Default);
    }

    public override void OnJoinedRoom()
    {
        Debug.Log($"[NetworkManager] Joined room: {PhotonNetwork.CurrentRoom.Name} " +
                  $"(players: {PhotonNetwork.CurrentRoom.PlayerCount})");
        OnRoomJoined?.Invoke();
    }

    public override void OnJoinRoomFailed(short returnCode, string message)
    {
        Debug.LogError($"[NetworkManager] Join room failed ({returnCode}): {message}");
    }

    public override void OnDisconnected(DisconnectCause cause)
    {
        Debug.LogWarning($"[NetworkManager] Disconnected: {cause}");
    }
}
