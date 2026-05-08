using UnityEngine;
using UnityEngine.XR.Management;

/// <summary>
/// アプリ起動時の最初のエントリポイント。
/// Awake()でNetworkManagerを生成し、ルーム参加後にRoleBasedBootSystemの設定に基づいて
/// LocalWorkerSetup または RemoteExpertSetup を呼び分ける。
/// Expert時はXRを停止し、Worker時はXRを確保する。
///
/// 再接続時(_setupDone == true)は再初期化をスキップし、代わりに軽量な
/// OnReconnected()パスを実行して実験の状態を復元する。
/// </summary>
public class SceneBootstrapper : MonoBehaviour
{
    private NetworkManager networkManager;
    [TextArea(3, 10)]
    public string note;

    [Header("Experiment")]
    [Tooltip("参加者番号 — ラテン方格の条件順序を決定します (n % 9)")]
    public int participantNumber = 0;

    [Header("Python (Expert PC only)")]
    [Tooltip("32-bit Python — Tobii/赤外線用 (noise_low)。例: C:/Python311_32/python.exe")]
    public string pythonExecutable32    = "python";
    [Tooltip("64-bit Python — ウェブカメラ/高ノイズ用。例: C:/Python311/python.exe")]
    public string pythonExecutable64    = "python";
    [Tooltip("EyeTrackToOSCData リポジトリのルートフォルダ。例: C:/Users/mtaku/EyeTrackToOSCData")]
    public string pythonScriptDirectory = "";
    [Tooltip("Tobii が手元にないときONにする。32-bit スクリプトの起動をスキップします。")]
    public bool   skipTobiiLaunch       = false;

    [Header("Python Script Args (Expert PC only)")]
    [Tooltip("Block 0 — Tobii 赤外線スクリプトの引数。通常は空。")]
    public string tobiiScriptArgs     = "";
    [Tooltip("Block 1 — ウェブカメラ実行スクリプトの引数。")]
    public string webcamScriptArgs    = "--weights models/L2CSNet_gaze360.pkl --osc-port 8000";
    [Tooltip("Block 2 — ハイノイズスクリプトの引数。通常は空。")]
    public string highNoiseScriptArgs = "";

    [Header("Python Calibration Args (Webcam only)")]
    [Tooltip("Webcam キャリブレーション引数（実行と同じスクリプト）。Tobii は手動キャリブレーションのため不要。")]
    public string webcamCalibArgs = "--calibrate --weights models/L2CSNet_gaze360.pkl --osc-port 0";

    // Guards against re-running full init on reconnect
    private bool _setupDone = false;
    private string _role;

    // References kept for reconnect path
    private LocalWorkerSetup  _workerSetup;
    private RemoteExpertSetup _expertSetup;

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
        if (_setupDone)
        {
            // ── Reconnect path ────────────────────────────────────────────
            // Full init was already done; just recover experiment state.
            OnReconnected();
            return;
        }

        // ── First-time init ───────────────────────────────────────────────

        // RoleBasedBootSystem からロールを取得
        RoleBasedBootSystem bootSystem = FindAnyObjectByType<RoleBasedBootSystem>();

        if (bootSystem != null)
        {
            _role = bootSystem.SelectedRole == AppRole.Expert
                ? RoleManager.ROLE_EXPERT
                : RoleManager.ROLE_WORKER;
            Debug.Log($"[SceneBootstrapper] Role from RoleBasedBootSystem: {_role}");
        }
        else
        {
            // フォールバック: ビルドターゲットで判断
#if UNITY_ANDROID
            _role = RoleManager.ROLE_WORKER;
#else
            _role = RoleManager.ROLE_EXPERT;
#endif
            Debug.Log($"[SceneBootstrapper] Role from build target (fallback): {_role}");
        }

        // ロールをPhotonに登録
        RoleManager.SetRole(_role);

        // XR制御: Expert時はXRを停止、Worker時はXRを確保
        ConfigureXR(_role);

        // 対応するSetupを起動
        if (_role == RoleManager.ROLE_WORKER)
        {
            _workerSetup = gameObject.AddComponent<LocalWorkerSetup>();
            _workerSetup.participantNumber = participantNumber;
            _workerSetup.Initialize();
        }
        else
        {
            _expertSetup = gameObject.AddComponent<RemoteExpertSetup>();
            _expertSetup.participantNumber     = participantNumber;
            _expertSetup.pythonExecutable32    = pythonExecutable32;
            _expertSetup.pythonExecutable64    = pythonExecutable64;
            _expertSetup.pythonScriptDirectory = pythonScriptDirectory;
            _expertSetup.skipTobiiLaunch       = skipTobiiLaunch;
            _expertSetup.tobiiScriptArgs       = tobiiScriptArgs;
            _expertSetup.webcamScriptArgs      = webcamScriptArgs;
            _expertSetup.highNoiseScriptArgs   = highNoiseScriptArgs;
            _expertSetup.webcamCalibArgs       = webcamCalibArgs;
            _expertSetup.Initialize();
        }

        _setupDone = true;
    }

    /// <summary>
    /// Lightweight reconnect handler.
    /// Does NOT re-instantiate prefabs or re-run calibration.
    /// Worker: sends SYNC_REQUEST → Expert re-broadcasts state + RemainingSeconds.
    /// Expert: re-broadcasts current state immediately.
    /// MeshHandler calibration is preserved automatically via Photon's AllBuffered RPC cache.
    /// </summary>
    private void OnReconnected()
    {
        Debug.Log("[SceneBootstrapper] Reconnected — restoring experiment state.");

        if (_role == RoleManager.ROLE_WORKER && _workerSetup != null)
        {
            _workerSetup.RequestStateSync();
        }
        else if (_role == RoleManager.ROLE_EXPERT && _expertSetup != null)
        {
            _expertSetup.BroadcastCurrentState();
        }
        else
        {
            Debug.LogWarning("[SceneBootstrapper] OnReconnected: no setup reference found. " +
                             "State recovery may be incomplete.");
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
