using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Loads UI messages from StreamingAssets/ui_messages.txt; edit the file to change strings without recompiling.
public static class MessageBank
{
    private static readonly Dictionary<string, string> s_messages = new();
    private static bool s_loaded;

    // ── Public API ─────────────────────────────────────────────────────────────

    public static string Get(string key)
    {
        EnsureLoaded();
        return s_messages.TryGetValue(key, out var val) ? val : key;
    }

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
        if (s_loaded) return;
        s_loaded = true;  // set before load to prevent re-entry on error

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
            Debug.Log($"[MessageBank] Loaded {s_messages.Count} messages from ui_messages.txt");
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
            s_messages[key] = val;
        }
    }
}
