using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using System;

// Singleton managing Photon connection and room joining; created by SceneBootstrapper2 with DontDestroyOnLoad.
public class NetworkManager : MonoBehaviourPunCallbacks
{
    public static NetworkManager Instance { get; private set; }

    public event Action OnRoomJoined;
    public event Action OnNetworkDisconnected;

    private const string k_roomName    = "CoGaze_Room";
    private const string k_fixedRegion = "asia";

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

    public void Connect()
    {
        if (PhotonNetwork.IsConnected)
        {
            Debug.Log("[NetworkManager] Already connected.");
            return;
        }

        PhotonNetwork.PhotonServerSettings.AppSettings.FixedRegion = k_fixedRegion;
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
        PhotonNetwork.JoinOrCreateRoom(k_roomName, options, TypedLobby.Default);
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

        // Don't reconnect on intentional or unrecoverable causes
        if (cause == DisconnectCause.DisconnectByClientLogic ||
            cause == DisconnectCause.ApplicationQuit           ||
            cause == DisconnectCause.InvalidAuthentication     ||
            cause == DisconnectCause.CustomAuthenticationFailed) return;

        OnNetworkDisconnected?.Invoke();
        StartCoroutine(ReconnectAfterDelay(3f));
    }

    private void OnDestroy()
    {
        Instance = null;
    }

    private System.Collections.IEnumerator ReconnectAfterDelay(float delay)
    {
        Debug.Log($"[NetworkManager] Reconnecting in {delay}s...");
        yield return new WaitForSeconds(delay);
        Connect();
    }
}
