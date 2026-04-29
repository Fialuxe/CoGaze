using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

/// <summary>
/// プレイヤーのロール (worker / expert) をPhoton CustomPropertiesに書き込み・読み出しするユーティリティ。
/// </summary>
public class RoleManager : MonoBehaviour
{
    public const string ROLE_KEY = "role";
    public const string ROLE_WORKER = "worker";
    public const string ROLE_EXPERT = "expert";

    /// <summary>ローカルプレイヤーのロール</summary>
    public static string LocalRole { get; private set; }

    /// <summary>
    /// ロールを設定し、Photon CustomPropertiesに書き込む。
    /// </summary>
    public static void SetRole(string role)
    {
        LocalRole = role;
        Hashtable props = new Hashtable { { ROLE_KEY, role } };
        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        Debug.Log($"[RoleManager] Role set to: {role}");
    }

    /// <summary>
    /// 指定プレイヤーのロールをCustomPropertiesから取得する。
    /// </summary>
    public static string GetPlayerRole(Player player)
    {
        if (player != null && player.CustomProperties.TryGetValue(ROLE_KEY, out object role))
        {
            return role as string;
        }
        return null;
    }
}
