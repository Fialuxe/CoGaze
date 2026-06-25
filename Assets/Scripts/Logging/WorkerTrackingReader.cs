using System.Globalization;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

/// <summary>
/// Utility class (no MonoBehaviour needed) that the Expert side uses to read
/// the Worker's real-time head / controller pose from Photon Custom Player Properties.
///
/// The Worker publishes three keys via WorkerTrackingSync:
///   "hPos"  — head world position  ("x,y,z" InvariantCulture)
///   "hFwd"  — head forward vector  ("x,y,z" InvariantCulture)
///   "rCtrl" — right (or left) controller world position ("x,y,z", or "" if unavailable)
///
/// Usage (Expert side):
///   if (WorkerTrackingReader.TryGetHeadPose(out Vector3 pos, out Vector3 fwd))
///       Debug.Log($"Worker head: {pos}, forward: {fwd}");
///
///   if (WorkerTrackingReader.TryGetControllerPosition(out Vector3 ctrlPos))
///       Debug.Log($"Worker controller: {ctrlPos}");
/// </summary>
public static class WorkerTrackingReader
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Try to read the Worker's head world position and forward direction.
    /// Returns false when no Worker is in the room or the properties are absent/stale.
    /// </summary>
    public static bool TryGetHeadPose(out Vector3 position, out Vector3 forward)
    {
        position = Vector3.zero;
        forward  = Vector3.forward;

        var worker = FindWorkerPlayer();
        if (worker == null) return false;

        bool gotPos = TryParseVec(worker, "hPos", out position);
        bool gotFwd = TryParseVec(worker, "hFwd", out forward);
        return gotPos && gotFwd;
    }

    /// <summary>
    /// Try to read the Worker's active controller world position
    /// (right controller preferred; left fallback; absent if neither tracked).
    /// Returns false when unavailable.
    /// </summary>
    public static bool TryGetControllerPosition(out Vector3 position)
    {
        position = Vector3.zero;

        var worker = FindWorkerPlayer();
        if (worker == null) return false;

        if (!worker.CustomProperties.TryGetValue("rCtrl", out object raw)) return false;
        var s = raw as string;
        if (string.IsNullOrEmpty(s)) return false;  // controller not tracked

        return TryParseVecString(s, out position);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Scan the current room's player list for the first player whose Photon
    /// custom property "role" == "worker".
    /// </summary>
    private static Player FindWorkerPlayer()
    {
        if (!PhotonNetwork.InRoom) return null;

        foreach (var kv in PhotonNetwork.CurrentRoom.Players)
        {
            var player = kv.Value;
            if (RoleManager.GetPlayerRole(player) == RoleManager.ROLE_WORKER)
                return player;
        }
        return null;
    }

    private static bool TryParseVec(Player player, string key, out Vector3 result)
    {
        result = Vector3.zero;
        if (!player.CustomProperties.TryGetValue(key, out object raw)) return false;
        return TryParseVecString(raw as string, out result);
    }

    /// <summary>
    /// Parse "x,y,z" produced by WorkerTrackingSync.FmtVec() using InvariantCulture.
    /// </summary>
    private static bool TryParseVecString(string s, out Vector3 result)
    {
        result = Vector3.zero;
        if (string.IsNullOrEmpty(s)) return false;

        var parts = s.Split(',');
        if (parts.Length != 3) return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;

        result = new Vector3(x, y, z);
        return true;
    }
}
