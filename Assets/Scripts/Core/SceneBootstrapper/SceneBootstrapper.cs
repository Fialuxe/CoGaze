using System.Collections;
using UnityEngine;
using UnityEngine.XR.Management;
using Photon.Voice.PUN;

/// <summary>
/// Application entry point.
/// Creates NetworkManager in Awake() and, after joining a room, calls
/// LocalWorkerSetup or RemoteExpertSetup based on the RoleBasedBootSystem setting.
/// Stops XR for Expert (PC) and ensures XR is running for Worker (Quest).
///
/// On disconnect the setup components are destroyed and _setupDone is reset,
/// so the next OnRoomJoined always runs full initialisation.
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
        ApplyRuntimeOptimizations();

        GameObject nmObj = new GameObject("NetworkManager");
        networkManager = nmObj.AddComponent<NetworkManager>();

        // PunVoiceClient must be on a root GameObject (DontDestroyOnLoad requirement).
        // Only add in code if not already present in the scene.
        var existingPvc = Object.FindAnyObjectByType<PunVoiceClient>();
        if (existingPvc == null)
        {
            nmObj.AddComponent<PunVoiceClient>();
        }
        else if (existingPvc.transform.parent != null)
        {
            Debug.LogError("[SceneBootstrapper] PunVoiceClient is on a child GameObject — " +
                           "move it to a root-level GameObject or it will fail DontDestroyOnLoad.");
        }

        networkManager.OnRoomJoined += OnRoomJoined;
        networkManager.OnNetworkDisconnected += OnPhotonDisconnected;

        networkManager.Connect();

        Debug.Log("[SceneBootstrapper] Initialized. Connecting to Photon...");
    }

    private void ApplyRuntimeOptimizations()
    {
        // Quest standard is 72 fps
        Application.targetFrameRate = 72;

        // Drop fixed-update rate from 50 Hz (0.02 s) to 25 Hz (0.04 s) —
        // this app has no physics simulation so we cut fixed update cost significantly.
        Time.fixedDeltaTime = 0.04f;
        Physics.defaultSolverIterations = 1;

        // Belt-and-suspenders shadow disable in case QualitySettings/URP profile is not stripped
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.shadowDistance = 0;

        // Strip realtime shadows from every light in the scene
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
        // Role detection and XR configuration must happen immediately on the main thread.
        DetectRole();
        RoleManager.SetRole(_role);
        ConfigureXR(_role);

        // Show device check screen before starting audio; setup runs after user confirms.
        StartCoroutine(SetupAfterDeviceCheck());
    }

    private void DetectRole()
    {
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
            // Fallback: infer role from build target when RoleBasedBootSystem is absent
#if UNITY_ANDROID
            _role = RoleManager.ROLE_WORKER;
#else
            _role = RoleManager.ROLE_EXPERT;
#endif
            Debug.Log($"[SceneBootstrapper] Role from build target (fallback): {_role}");
        }
    }

    private IEnumerator SetupAfterDeviceCheck()
    {
        string selectedDevice = null;
        bool   confirmed      = false;

        var checker = gameObject.AddComponent<AudioDeviceChecker>();
        checker.OnDeviceConfirmed += dev => { selectedDevice = dev; confirmed = true; };
        checker.Initialize(_role == RoleManager.ROLE_EXPERT);

        yield return new WaitUntil(() => confirmed);

        if (_role == RoleManager.ROLE_WORKER)
        {
            _workerSetup = gameObject.AddComponent<LocalWorkerSetup>();
            _workerSetup.participantNumber  = participantNumber;
            _workerSetup.preferredMicDevice = selectedDevice;
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
            _expertSetup.preferredMicDevice    = selectedDevice;
            _expertSetup.Initialize();
        }

        _setupDone = true;
    }

    private void OnPhotonDisconnected()
    {
        if (!_setupDone) return;
        Debug.Log("[SceneBootstrapper] Network lost — resetting for full re-init on reconnect.");
        if (_workerSetup != null) { Destroy(_workerSetup); _workerSetup = null; }
        if (_expertSetup != null) { Destroy(_expertSetup); _expertSetup = null; }
        _setupDone = false;
    }

    private void ConfigureXR(string role)
    {
        var xrSettings = XRGeneralSettings.Instance;
        if (xrSettings == null || xrSettings.Manager == null) return;

        if (role == RoleManager.ROLE_EXPERT)
        {
            if (xrSettings.Manager.isInitializationComplete)
            {
                xrSettings.Manager.StopSubsystems();
                xrSettings.Manager.DeinitializeLoader();
                Debug.Log("[SceneBootstrapper] XR stopped for Expert role.");
            }
        }
        else
        {
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
            networkManager.OnNetworkDisconnected -= OnPhotonDisconnected;
        }
    }
}
