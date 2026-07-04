using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// Pre-flight checks shown on startup screens; Fatal blocks confirm, Warning flags. Shared by StartupUI and WorkerStartupPanel.
public static class StartupSelfCheck
{
    public enum Severity { Fatal, Warning, Info }

    public struct Issue
    {
        public Severity Severity;
        public string   Message;
        public Issue(Severity s, string m) { Severity = s; Message = m; }
    }

    /// <param name="includeInstructions">
    /// Desktop checks the instructions file directly; on Android (Quest) StreamingAssets lives
    /// inside the APK and isn't a readable File path, so the Worker skips it (the Expert authority
    /// covers it). Callers pass false on the headset.
    /// </param>
    /// <param name="micDevice">
    /// Unity Microphone name selected in StartupUI, or null to skip the mic check (Worker). The
    /// Expert sends voice through Photon's native Windows capture, which ignores Unity names —
    /// this verifies the selection maps to a native device so the mic-test meter (Unity path)
    /// isn't the only, misleading, signal.
    /// </param>
    /// <param name="checkParticipantId">
    /// false on the Worker: its local config id is only a stale fallback (the real id is adopted
    /// from the Expert's room properties at join), so the empty-id Fatal and the existing-logs-dir
    /// warning would mislead the operator about a value that is never used.
    /// </param>
    public static List<Issue> Run(string participantId, int orderIndex, bool includeInstructions, string micDevice = null, bool checkParticipantId = true)
    {
        var issues = new List<Issue>();

        // ── Photon native mic mapping (Windows Expert only) ──
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (!string.IsNullOrEmpty(micDevice) && micDevice != "(no microphone found)")
        {
            issues.Add(PhotonMicDeviceResolver.TryResolve(micDevice, out var nativeMic, out string micDetail)
                ? new Issue(Severity.Info,    $"✓ 送信マイク（ネイティブ）: {nativeMic}")
                : new Issue(Severity.Warning, $"送信マイクをネイティブ列挙で特定できません（{micDetail}）— Windows既定マイクで送信されます"));
        }
#endif

        // ── participant id (Fatal) ──
        if (checkParticipantId && string.IsNullOrWhiteSpace(participantId))
            issues.Add(new Issue(Severity.Fatal, "参加者IDが未入力です"));

        // ── instructions_new.txt (Fatal, Desktop only) ──
        if (includeInstructions)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "instructions_new.txt");
            bool ok = false;
            try { ok = File.Exists(path) && new FileInfo(path).Length > 0; } catch { ok = false; }
            issues.Add(ok
                ? new Issue(Severity.Info,  "✓ instructions_new.txt")
                : new Issue(Severity.Fatal, "instructions_new.txt が見つからない/空です"));
        }

        // ── SharedMesh / calibration system (Fatal) ──
        bool meshOk = Object.FindAnyObjectByType<MeshHandler>() != null;
        issues.Add(meshOk
            ? new Issue(Severity.Info,  "✓ SharedMesh (MeshHandler)")
            : new Issue(Severity.Fatal, "SharedMesh/MeshHandler がシーンにありません"));

        // ── existing participant log dir (Warning: CSV append/overwrite) ──
        if (checkParticipantId && !string.IsNullOrWhiteSpace(participantId))
        {
            string logDir = Path.Combine(Application.persistentDataPath, "logs", participantId);
            bool exists = false;
            try { exists = Directory.Exists(logDir); } catch { exists = false; }
            if (exists)
                issues.Add(new Issue(Severity.Warning, $"logs/{participantId} が既に存在（CSVに追記されます）"));
        }

        // ── nature sound (Warning) ──
        if (Resources.Load<AudioClip>("Audio/rain_loop") == null)
            issues.Add(new Issue(Severity.Warning, "rain_loop 音源なし（ブラウンノイズのみ）"));

        // ── condition-order preview (Info) ──
        issues.Add(new Issue(Severity.Info, "条件順: " + ConditionOrderPreview(orderIndex)));

        return issues;
    }

    public static bool HasFatal(List<Issue> issues)
    {
        if (issues == null) return false;
        foreach (var i in issues) if (i.Severity == Severity.Fatal) return true;
        return false;
    }

    public static int CountFatal(List<Issue> issues)
    {
        if (issues == null) return 0;
        int n = 0;
        foreach (var i in issues) if (i.Severity == Severity.Fatal) n++;
        return n;
    }

    public static string ConditionOrderPreview(int orderIndex)
    {
        var order = ExperimentDesign.ComputeOrder(orderIndex);
        var sb = new StringBuilder();
        for (int i = 0; i < order.Length; i++)
        {
            if (i > 0) sb.Append(" → ");
            sb.Append(ExperimentDesign.Conditions[order[i]].name);
        }
        return sb.ToString();
    }
}
