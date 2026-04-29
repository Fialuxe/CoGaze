using UnityEngine;
using UnityEngine.XR.Management;

/// <summary>
/// アプリ起動時の最初のエントリポイント。
/// Awake()でNetworkManagerを生成し、ルーム参加後にRoleBasedBootSystemの設定に基づいて
/// LocalWorkerSetup または RemoteExpertSetup を呼び分ける。
/// Expert時はXRを停止し、Worker時はXRを確保する。
/// </summary>
public class SceneBootstrapper : MonoBehaviour
{
    private NetworkManager networkManager;
    [TextArea(3, 10)] 
    public string note;

    private void Awake()
    {
        // ==========================================
        // 実行時パフォーマンス最適化（特にQuest向け）
        // ==========================================
        ApplyRuntimeOptimizations();

        // NetworkManagerを生成
        GameObject nmObj = new GameObject("NetworkManager");
        networkManager = nmObj.AddComponent<NetworkManager>();
        // DontDestroyOnLoad は NetworkManager.Awake() 内で処理済み

        // ルーム参加イベントを購読
        networkManager.OnRoomJoined += OnRoomJoined;

        // 接続開始
        networkManager.Connect();

        Debug.Log("[SceneBootstrapper] Initialized. Connecting to Photon...");
    }

    private void ApplyRuntimeOptimizations()
    {
        // 1. ターゲットFPS設定（Quest標準は72fps、最低30fpsを保証するための措置）
        Application.targetFrameRate = 72;

        // 2. 物理演算の更新頻度を下げる（デフォルト0.02s=50Hz → 0.04s=25Hz）
        //    このアプリは物理シミュレーション不要なので、負荷を大幅に削減できる
        Time.fixedDeltaTime = 0.04f;
        Physics.defaultSolverIterations = 1;

        // 3. 影を完全無効化（QualitySettings/URP設定のフォールバック）
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.shadowDistance = 0;

        // 4. シーン内の全ライトからリアルタイム影を剥がす
        Light[] allLights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (var light in allLights)
        {
            light.shadows = LightShadows.None;
        }

        Debug.Log($"[SceneBootstrapper] Runtime optimizations applied. " +
            $"TargetFPS={Application.targetFrameRate}, FixedDT={Time.fixedDeltaTime}, " +
            $"Shadows=Disabled, Lights optimized={allLights.Length}");
    }

    private void OnRoomJoined()
    {
        // RoleBasedBootSystem からロールを取得
        RoleBasedBootSystem bootSystem = FindAnyObjectByType<RoleBasedBootSystem>();

        string role;
        if (bootSystem != null)
        {
            role = bootSystem.SelectedRole == AppRole.Expert
                ? RoleManager.ROLE_EXPERT
                : RoleManager.ROLE_WORKER;
            Debug.Log($"[SceneBootstrapper] Role from RoleBasedBootSystem: {role}");
        }
        else
        {
            // フォールバック: ビルドターゲットで判断
#if UNITY_ANDROID
            role = RoleManager.ROLE_WORKER;
#else
            role = RoleManager.ROLE_EXPERT;
#endif
            Debug.Log($"[SceneBootstrapper] Role from build target (fallback): {role}");
        }

        // ロールをPhotonに登録
        RoleManager.SetRole(role);

        // XR制御: Expert時はXRを停止、Worker時はXRを確保
        ConfigureXR(role);

        // 対応するSetupを起動
        if (role == RoleManager.ROLE_WORKER)
        {
            var setup = gameObject.AddComponent<LocalWorkerSetup>();
            setup.Initialize();
        }
        else
        {
            var setup = gameObject.AddComponent<RemoteExpertSetup>();
            setup.Initialize();
        }
    }

    /// <summary>
    /// ロールに応じてXRサブシステムを制御する。
    /// Expert (PC) ではXRを停止してMeta Questへの接続を防ぐ。
    /// </summary>
    private void ConfigureXR(string role)
    {
        var xrSettings = XRGeneralSettings.Instance;
        if (xrSettings == null || xrSettings.Manager == null) return;

        if (role == RoleManager.ROLE_EXPERT)
        {
            // Expert: XRを停止（HMDに接続しない）
            if (xrSettings.Manager.isInitializationComplete)
            {
                xrSettings.Manager.StopSubsystems();
                xrSettings.Manager.DeinitializeLoader();
                Debug.Log("[SceneBootstrapper] XR stopped for Expert role.");
            }
        }
        else
        {
            // Worker: XRが未起動なら起動
            if (!xrSettings.Manager.isInitializationComplete)
            {
                xrSettings.Manager.InitializeLoaderSync();
                xrSettings.Manager.StartSubsystems();
                Debug.Log("[SceneBootstrapper] XR started for Worker role.");
            }
        }
    }

    private void OnDestroy()
    {
        if (networkManager != null)
        {
            networkManager.OnRoomJoined -= OnRoomJoined;
        }
    }
}
