using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;

// Singleton managing Photon connection and room joining; created by SceneBootstrapper2 with DontDestroyOnLoad.
public class NetworkManager : MonoBehaviourPunCallbacks
{
    public static NetworkManager Instance { get; private set; }

    public event Action OnRoomJoined;
    public event Action OnNetworkDisconnected;

    // Whether the experiment room is currently listed in the lobby (i.e. the Expert has already
    // started and created it). null = unknown (no room list received yet). Only maintained while
    // in the lobby during ConnectForRoomPreview(); reset on disconnect.
    public bool? ExpertRoomVisible { get; private set; }

    private const string k_roomName    = "CoGaze_Room";
    private const string k_fixedRegion = "asia";

    // false while the Worker startup panel is previewing the lobby (room list only);
    // OnConnectedToMaster then joins the lobby instead of the room. JoinExperimentRoom()
    // flips it back to true.
    private bool _joinRoomOnConnect = true;

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

    // Connect to master + lobby WITHOUT joining the room. Lets the Worker startup panel verify
    // connectivity and whether the Expert's room already exists before the operator confirms.
    public void ConnectForRoomPreview()
    {
        if (PhotonNetwork.InRoom) return;
        _joinRoomOnConnect = false;
        if (PhotonNetwork.IsConnected)
        {
            if (PhotonNetwork.IsConnectedAndReady && !PhotonNetwork.InLobby)
                PhotonNetwork.JoinLobby();
            return;
        }
        ConnectInternal();
    }

    // Join the experiment room from any prior state: fresh boot (connect first) or lobby preview
    // (join directly). Safe to call regardless of what ConnectForRoomPreview() did.
    public void JoinExperimentRoom()
    {
        _joinRoomOnConnect = true;
        if (PhotonNetwork.InRoom) return;
        if (PhotonNetwork.IsConnectedAndReady)
            JoinRoomNow();
        else if (!PhotonNetwork.IsConnected)
            ConnectInternal();
        // else: connection already in progress — OnConnectedToMaster joins the room.
    }

    private void ConnectInternal()
    {
        PhotonNetwork.PhotonServerSettings.AppSettings.FixedRegion = k_fixedRegion;
        PhotonNetwork.ConnectUsingSettings();
        Debug.Log("[NetworkManager] Connecting to Photon (region: asia)...");
    }

    private void JoinRoomNow()
    {
        RoomOptions options = new RoomOptions
        {
            MaxPlayers = 4,
            IsVisible = true,
            IsOpen = true
        };
        PhotonNetwork.JoinOrCreateRoom(k_roomName, options, TypedLobby.Default);
    }

    public override void OnConnectedToMaster()
    {
        if (_joinRoomOnConnect)
        {
            Debug.Log("[NetworkManager] Connected to Master. Joining room...");
            JoinRoomNow();
        }
        else
        {
            Debug.Log("[NetworkManager] Connected to Master. Joining lobby (room preview)...");
            PhotonNetwork.JoinLobby();
        }
    }

    // Lobby room list. First update is the full list, later ones are deltas — only touch the flag
    // when our room appears in the delta, except the very first update which resolves "unknown".
    public override void OnRoomListUpdate(List<RoomInfo> roomList)
    {
        foreach (var room in roomList)
        {
            if (room.Name != k_roomName) continue;
            ExpertRoomVisible = !room.RemovedFromList;
        }
        if (ExpertRoomVisible == null) ExpertRoomVisible = false;
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
        ExpertRoomVisible = null;   // lobby knowledge is stale once disconnected

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
        // Preserve the current mode (room vs lobby preview) — a mid-panel reconnect must not
        // suddenly join the room before the operator confirmed.
        if (!PhotonNetwork.IsConnected) ConnectInternal();
    }
}
