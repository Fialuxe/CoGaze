using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

// Reads/writes player role (worker / expert) to Photon CustomProperties.
public class RoleManager : MonoBehaviour
{
    public const string ROLE_KEY = "role";
    public const string ROLE_WORKER = "worker";
    public const string ROLE_EXPERT = "expert";

    public static string LocalRole { get; private set; }

    public static void SetRole(string role)
    {
        LocalRole = role;
        Hashtable props = new Hashtable { { ROLE_KEY, role } };
        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        Debug.Log($"[RoleManager] Role set to: {role}");
    }

    public static string GetPlayerRole(Player player)
    {
        if (player != null && player.CustomProperties.TryGetValue(ROLE_KEY, out object role))
        {
            return role as string;
        }
        return null;
    }
}
