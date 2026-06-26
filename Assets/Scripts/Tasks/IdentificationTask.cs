using UnityEngine;
using Photon.Pun;

public class IdentificationTask : MonoBehaviourPun
{
    public event System.Action       OnTaskComplete;
    public event System.Action<bool> OnQRStateChanged;

    private ExperimentManager2 experimentManager2;
    public string CompletedMarkerId { get; private set; }

    private bool               _doneSent       = false;
    private bool               _qrScanned      = false;
    private bool               _workerInitDone = false;
    private QRSpatialManager   _qrManager;

    private bool IsWorker => RoleManager.LocalRole == RoleManager.ROLE_WORKER;

#if UNITY_ANDROID && !UNITY_EDITOR
    // Analog index-trigger threshold. Reuses the shared grip threshold value because the
    // same Touch Plus axis inconsistency (MQ3 vs MQ3S — identical controllers, differing
    // firmware, differing raw value at "fully pressed") applies to the index trigger too.
    private const float IndexThreshold     = OVRInputThresholds.Grip;
    private const float ProximityThreshold = 0.20f; // 20 cm
    private bool         _triggerWasDown   = false;
    private OVRCameraRig _ovrRig;
#endif

    private void Start()
    {
        experimentManager2 = Object.FindAnyObjectByType<ExperimentManager2>();
        if (experimentManager2 == null)
        {
            Debug.LogError("[IdentificationTask] ExperimentManager2 not found in scene.");
            return;
        }

        experimentManager2.OnStateChanged += OnStateChanged;
        SetTaskEnabled(false);
    }

    private void EnsureWorkerInit()
    {
        if (_workerInitDone) return;
        _workerInitDone = true;
        if (!IsWorker) return;

        _qrManager = Object.FindAnyObjectByType<QRSpatialManager>();
        if (_qrManager != null)
            _qrManager.OnMarkerDetected += OnQRMarkerDetected;
        else
            Debug.LogWarning("[IdentificationTask] QRSpatialManager not found — QR gate disabled.");
    }

    private void OnDestroy()
    {
        if (experimentManager2 != null)
            experimentManager2.OnStateChanged -= OnStateChanged;
        if (_qrManager != null)
            _qrManager.OnMarkerDetected -= OnQRMarkerDetected;
    }

    private void OnQRMarkerDetected(string markerId, Vector3 pos, Quaternion rot)
    {
        if (_qrScanned) return;
        if (markerId.StartsWith("QR_CALIB")) return;
        _qrScanned = true;
        OnQRStateChanged?.Invoke(true);
        Debug.Log($"[IdentificationTask] QR confirmed (id='{markerId}') — pull the index trigger near it to complete.");
    }

    private void OnStateChanged(ExperimentState newState)
    {
        EnsureWorkerInit();
        bool shouldRun = newState == ExperimentState.TaskRunning
                      && experimentManager2.CurrentStepType == StepType.Task;

        if (shouldRun) StartTask();
        else           EndTask();
    }

    public void StartTask()
    {
        Debug.Log("[IdentificationTask] StartTask");
        _qrScanned = false;
        CompletedMarkerId = null;
        // Detection-based arming was removed: the periodic QR re-broadcast stops after
        // dual-QR calibration, so marker-detected events may never re-fire during a trial.
        // Markers found during Setup persist in QRSpatialManager.DetectedMarkers with stable
        // SharedMesh-anchored positions, so the index trigger completes the task by proximity
        // at trigger time. Show the "approach + pull trigger" hint immediately rather than
        // waiting for a detection event that may never come.
        OnQRStateChanged?.Invoke(true);
#if UNITY_ANDROID && !UNITY_EDITOR
        _triggerWasDown = false;
#endif
        SetTaskEnabled(true);
    }

    public void EndTask()
    {
        Debug.Log("[IdentificationTask] EndTask");
        SetTaskEnabled(false);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void Update()
    {
        if (!IsWorker) return;

        float trigger     = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        bool  triggerDown = trigger > IndexThreshold;
        bool  justPressed = triggerDown && !_triggerWasDown;
        _triggerWasDown = triggerDown;

        // While the left X button is held the right grip is calibrating the mesh (MeshHandler).
        // The answer action is now the index trigger so it no longer shares the grip, but keep
        // this guard so an answer can't be registered during a hold-X calibration grab.
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) return;

        if (!justPressed) return;
        if (_qrManager == null) return;

        Vector3 controllerPos = GetRightControllerWorldPos();
        foreach (var kvp in _qrManager.DetectedMarkers)
        {
            if (kvp.Key.StartsWith("QR_CALIB")) continue;
            if (kvp.Value == null) continue;
            if (Vector3.Distance(controllerPos, kvp.Value.transform.position) < ProximityThreshold)
            {
                CompleteTask(kvp.Key);
                return;
            }
        }

        Debug.Log($"[IdentificationTask] Index trigger pulled but no QR within {ProximityThreshold * 100:F0} cm.");
    }

    private Vector3 GetRightControllerWorldPos()
    {
        if (_ovrRig == null)
            _ovrRig = FindAnyObjectByType<OVRCameraRig>();

        if (_ovrRig != null)
            return _ovrRig.rightHandAnchor.position;

        // Fallback if OVRCameraRig is absent (editor-side stub, etc.)
        return OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
    }
#endif

    private void CompleteTask(string markerId)
    {
        if (_doneSent) return;
        _doneSent = true;
        Debug.Log($"[IdentificationTask] Completion confirmed near QR '{markerId}' — sending RPC.");
        photonView.RPC(nameof(RPC_IdentificationDone), RpcTarget.All, markerId);
    }

    [PunRPC]
    private void RPC_IdentificationDone(string markerId)
    {
        CompletedMarkerId = markerId;
        Debug.Log($"[IdentificationTask] RPC_IdentificationDone received. markerId='{markerId}'");
        OnTaskComplete?.Invoke();
        SetTaskEnabled(false);
    }

    private void SetTaskEnabled(bool value)
    {
        enabled = value;
        if (value) _doneSent = false;
    }
}
