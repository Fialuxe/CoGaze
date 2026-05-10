using System;
using UnityEngine;

/// <summary>
/// Scene bootstrapper for the ReplayScene.
/// Attach this component to a single empty GameObject in the scene.
/// Awake() wires up all replay components — no other manual setup needed.
///
/// Optionally add a floor plane with a MeshCollider so Circle mode
/// can raycast against something during replay.
/// </summary>
public class ReplayBootstrapper : MonoBehaviour
{
    [Header("Log Folder")]
    [Tooltip("P{n} フォルダを指定すると起動時に自動でトライアル一覧をロードします。\n例: C:/Users/mtaku/AppData/LocalLow/DefaultCompany/CoGaze/logs/P0")]
    public string logFolder = "";

    private void Awake()
    {
        try
        {
            var mgr  = gameObject.AddComponent<ReplayManager>();
            var gaze = gameObject.AddComponent<ReplayGazeDriver>();
            var hand = gameObject.AddComponent<ReplayHandDriver>();
            var ui   = gameObject.AddComponent<ReplayLoader>();

            gaze.Initialize(mgr);
            hand.Initialize(mgr);
            ui.Initialize(mgr);

            if (!string.IsNullOrWhiteSpace(logFolder))
                ui.OpenFolder(logFolder.Trim());

            Debug.Log("[ReplayBootstrapper] Replay scene ready.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ReplayBootstrapper] Initialization failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
