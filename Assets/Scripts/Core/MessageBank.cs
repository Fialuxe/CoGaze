using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Loads UI messages from StreamingAssets/ui_messages.txt at startup.
/// Edit ui_messages.txt to change experiment messages without recompiling Unity.
///
/// Usage:
///   MessageBank.Get("calib.pass")
///   MessageBank.Format("calib.marginal", ("errX", "0.03"), ("errY", "0.04"))
///   MessageBank.Format("ui.finished.detail", ("participantId", "P01"))
/// </summary>
public static class MessageBank
{
    private static readonly Dictionary<string, string> _messages = new();
    private static bool _loaded;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Get a message by key. Returns the key itself if not found.</summary>
    public static string Get(string key)
    {
        EnsureLoaded();
        return _messages.TryGetValue(key, out var val) ? val : key;
    }

    /// <summary>
    /// Get a message and substitute {placeholder} values.
    /// Example: Format("calib.marginal", ("errX", "0.03"), ("errY", "0.04"))
    /// </summary>
    public static string Format(string key, params (string placeholder, string value)[] args)
    {
        string s = Get(key);
        foreach (var (k, v) in args)
            s = s.Replace("{" + k + "}", v);
        return s;
    }

    // ── Loading ────────────────────────────────────────────────────────────────

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;  // set before load to prevent re-entry on error

        string path = Path.Combine(Application.streamingAssetsPath, "ui_messages.txt");

#if UNITY_ANDROID && !UNITY_EDITOR
        // Android: StreamingAssets is inside the APK — cannot use File.ReadAllText.
        // Messages will fall back to the key names. Wire a UnityWebRequest load in
        // SceneBootstrapper2 if Worker-side UI messages are needed.
        Debug.Log("[MessageBank] Android: skipping ui_messages.txt load (Expert-only UI).");
        return;
#else
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[MessageBank] ui_messages.txt not found at: {path}");
            return;
        }

        try
        {
            Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            Debug.Log($"[MessageBank] Loaded {_messages.Count} messages from ui_messages.txt");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MessageBank] Failed to load ui_messages.txt: {ex.Message}");
        }
#endif
    }

    private static void Parse(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#") || line.Length == 0) continue;

            int eq = line.IndexOf('=');
            if (eq < 1) continue;

            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
            _messages[key] = val;
        }
    }
}
