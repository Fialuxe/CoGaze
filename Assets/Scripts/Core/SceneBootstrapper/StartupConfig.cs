using System.IO;
using UnityEngine;

// JSON-backed startup config persisted to Application.persistentDataPath/cogaze_config.json.
[System.Serializable]
public class StartupConfig
{
    public string participantId         = "P00";
    public int    participantOrderIndex = 0;
    // Resume support: number of already-completed conditions to skip (0 = normal full run).
    // Set per-boot in StartupUI; deliberately never restored into the UI so a stale value can't
    // silently skip conditions for the next participant.
    public int    startConditionOffset  = 0;
    public string pythonHost            = "127.0.0.1";
    public string microphoneDevice      = "";   // "" = use first available
    public bool   offlineMode           = false; // skip Photon, for local testing
    public string pythonScriptDir       = "";   // root dir of WebcamEyeTracking repo (for auto-launch)

    private static string ConfigPath =>
        Path.Combine(Application.persistentDataPath, "cogaze_config.json");

    public static StartupConfig LoadOrDefault()
    {
        try
        {
            string path = ConfigPath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonUtility.FromJson<StartupConfig>(json);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[StartupConfig] Failed to load config, using defaults: {ex.Message}");
        }

        return new StartupConfig();
    }

    public void Save()
    {
        try
        {
            string json = JsonUtility.ToJson(this, prettyPrint: true);
            File.WriteAllText(ConfigPath, json);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[StartupConfig] Failed to save config: {ex.Message}");
        }
    }
}
