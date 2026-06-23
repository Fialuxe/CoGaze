using UnityEngine;
using Photon.Pun;

public class IdentificationTask : MonoBehaviourPun
{
    public event System.Action       OnTaskComplete;
    public event System.Action<bool> OnQRStateChanged;

    private ExperimentManager2 experimentManager2;
    private bool               _doneSent       = false;
    private bool               _qrScanned      = false;
    private bool               _workerInitDone = false;
    private QRSpatialManager   _qrManager;

    private bool IsWorker => RoleManager.LocalRole == RoleManager.ROLE_WORKER;

#if UNITY_ANDROID && !UNITY_EDITOR
    // Analog grip threshold — same value as MeshHandler to handle Touch Plus axis
    // inconsistency on both MQ3 and MQ3S (identical Touch Plus controllers, but
    // firmware versions differ and the raw axis value at "fully squeezed" varies).
    private const float GripThreshold      = 0.7f;
    private const float ProximityThreshold = 0.20f; // 20 cm
    private bool         _gripWasDown      = false;
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
        if (markerId.StartsWith("calib")) return;
        _qrScanned = true;
        OnQRStateChanged?.Invoke(true);
        Debug.Log($"[IdentificationTask] QR confirmed (id='{markerId}') — squeeze grip near it to complete.");
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
        OnQRStateChanged?.Invoke(false);
#if UNITY_ANDROID && !UNITY_EDITOR
        _gripWasDown = false;
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
        if (!_qrScanned) return;

        float grip        = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool  gripDown    = grip > GripThreshold;
        bool  justPressed = gripDown && !_gripWasDown;
        _gripWasDown = gripDown;

        if (!justPressed) return;
        if (_qrManager == null) return;

        Vector3 controllerPos = GetRightControllerWorldPos();
        foreach (var kvp in _qrManager.DetectedMarkers)
        {
            if (kvp.Key.StartsWith("calib")) continue;
            if (kvp.Value == null) continue;
            if (Vector3.Distance(controllerPos, kvp.Value.transform.position) < ProximityThreshold)
            {
                CompleteTask(kvp.Key);
                return;
            }
        }

        Debug.Log($"[IdentificationTask] Grip pressed but no QR within {ProximityThreshold * 100:F0} cm.");
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
        photonView.RPC(nameof(RPC_IdentificationDone), RpcTarget.All);
    }

    [PunRPC]
    private void RPC_IdentificationDone()
    {
        Debug.Log("[IdentificationTask] RPC_IdentificationDone received.");
        OnTaskComplete?.Invoke();
        SetTaskEnabled(false);
    }

    private void SetTaskEnabled(bool value)
    {
        enabled = value;
        if (value) _doneSent = false;
    }
}
