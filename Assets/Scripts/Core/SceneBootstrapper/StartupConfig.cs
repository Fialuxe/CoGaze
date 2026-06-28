using System.IO;
using UnityEngine;

/// <summary>
/// JSON-backed persistent config that stores experiment startup parameters.
/// Persists across sessions via Application.persistentDataPath.
///
/// Usage:
///   var cfg = StartupConfig.LoadOrDefault();
///   cfg.participantId = "P01";
///   cfg.Save();
/// </summary>
[System.Serializable]
public class StartupConfig
{
    public string participantId         = "P00";
    public int    participantOrderIndex = 0;
    public string pythonHost            = "127.0.0.1";
    public string microphoneDevice      = "";   // "" = use first available
    public bool   offlineMode           = false; // skip Photon, for local testing
    public string pythonScriptDir       = "";   // root dir of WebcamEyeTracking repo (for auto-launch)

    private static string ConfigPath =>
        Path.Combine(Application.persistentDataPath, "cogaze_config.json");

    /// <summary>
    /// Loads config from disk if it exists; otherwise returns a new instance with defaults.
    /// Never throws — falls back to defaults on any error.
    /// </summary>
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

    /// <summary>
    /// Serializes this config to disk as pretty-printed JSON.
    /// No-op on error — logs a warning instead of throwing.
    /// </summary>
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
