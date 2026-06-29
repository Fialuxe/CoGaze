using System.Globalization;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

// Reads Worker head/controller pose from Photon Custom Player Properties ("hPos", "hFwd", "rCtrl"); Expert-side only.
public static class WorkerTrackingReader
{
    // ── Public API ────────────────────────────────────────────────────────────

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
