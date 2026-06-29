using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;

public class IdentificationTask : MonoBehaviourPun
{
    // Fired at task end (backward compat — ExperimentManager2 no longer uses this for advancing)
    public event System.Action                               OnTaskComplete;
    // True = task armed (start of task); false = wrong/searching (not currently used post-redesign)
    public event System.Action<bool>                         OnQRStateChanged;
    // (targetId, score) — Expert shows both; Worker shows score only (never expose targetId to Worker display)
    public event System.Action<string, int>                  OnTargetChanged;
    // Fires on each correct grip — WorkerHUD2 uses this for haptic + flash feedback
    public event System.Action                               OnCorrectGrip;
    // (targetId, grippedId, correct, scoreAfter) — ExperimentLogger writes identifications.csv
    public event System.Action<string, string, bool, int>    OnIdentificationAttempt;

    public const string QR_CALIB_PREFIX = "QR_CALIB";

    private ExperimentManager2 _experimentManager2;
    public string CompletedMarkerId { get; private set; }
    public string CurrentTargetId   { get; private set; }
    public int    Score             { get; private set; }
    public int    MissCount         { get; private set; }

    private bool             _workerInitDone;
    private QRSpatialManager _qrManager;

    private bool IsWorker => RoleManager.LocalRole == RoleManager.ROLE_WORKER;

#if UNITY_ANDROID && !UNITY_EDITOR
    // Answer = right-hand GRIP near the target QR (matches participant briefing video).
    // Calibration uses the index TRIGGER (MeshHandler) — inputs never overlap.
    private const float k_gripThreshold      = OVRInputThresholds.Grip;
    private const float k_proximityThreshold = 0.20f; // 20 cm
    private bool        _gripWasDown;
    private OVRCameraRig _ovrRig;
#endif

    private void Start()
    {
        _experimentManager2 = Object.FindAnyObjectByType<ExperimentManager2>();
        if (_experimentManager2 == null)
        {
            Debug.LogError("[IdentificationTask] ExperimentManager2 not found in scene.");
            return;
        }
        _experimentManager2.OnStateChanged += OnStateChanged;
        SetTaskEnabled(false);
    }

    private void EnsureWorkerInit()
    {
        if (_workerInitDone) return;
        _workerInitDone = true;
        if (!IsWorker) return;

        _qrManager = Object.FindAnyObjectByType<QRSpatialManager>();
        if (_qrManager == null)
            Debug.LogWarning("[IdentificationTask] QRSpatialManager not found — target selection disabled.");
    }

    private void OnDestroy()
    {
        if (_experimentManager2 != null)
            _experimentManager2.OnStateChanged -= OnStateChanged;
    }

    private void OnStateChanged(ExperimentState newState)
    {
        EnsureWorkerInit();
        bool shouldRun = newState == ExperimentState.TaskRunning
                      && _experimentManager2.CurrentStepType == StepType.Task;
        if (shouldRun) StartTask();
        else           EndTask();
    }

    public void StartTask()
    {
        Debug.Log("[IdentificationTask] StartTask");
        Score             = 0;
        MissCount         = 0;
        CurrentTargetId   = null;
        CompletedMarkerId = null;
        OnQRStateChanged?.Invoke(true);
#if UNITY_ANDROID && !UNITY_EDITOR
        _gripWasDown = false;
#endif
        SetTaskEnabled(true);
        if (IsWorker) SelectNextTarget();
    }

    // Worker picks a random non-calibration QR that differs from the current target.
    // Result is broadcast to all clients via RPC_SetTarget so Expert also knows the target.
    private void SelectNextTarget()
    {
        if (_qrManager == null) return;
        var candidates = new List<string>();
        foreach (var kvp in _qrManager.DetectedMarkers)
        {
            if (kvp.Key.StartsWith(QR_CALIB_PREFIX)) continue;
            if (kvp.Value == null) continue;
            candidates.Add(kvp.Key);
        }
        if (candidates.Count == 0)
        {
            Debug.LogWarning("[IdentificationTask] No non-calib QR candidates — target not set.");
            return;
        }
        string next = CurrentTargetId;
        if (candidates.Count > 1)
        {
            int tries = 0;
            while (next == CurrentTargetId && tries++ < 10)
                next = candidates[Random.Range(0, candidates.Count)];
        }
        else
            next = candidates[0];
        photonView.RPC(nameof(RPC_SetTarget), RpcTarget.All, next, Score);
    }

    [PunRPC]
    private void RPC_SetTarget(string targetId, int score)
    {
        CurrentTargetId = targetId;
        Score = score;
        Debug.Log($"[IdentificationTask] Target → '{targetId}' (score={score})");
        OnTargetChanged?.Invoke(targetId, score);
    }

    public void EndTask()
    {
        Debug.Log($"[IdentificationTask] EndTask. FinalScore={Score}");
        SetTaskEnabled(false);
        OnTaskComplete?.Invoke();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void Update()
    {
        if (!IsWorker) return;

        float grip        = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool  gripDown    = grip > k_gripThreshold;
        bool  justPressed = gripDown && !_gripWasDown;
        _gripWasDown = gripDown;

        // While X (left) is held, the right hand calibrates the mesh — block answer input.
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) return;
        if (!justPressed) return;
        if (_qrManager == null) return;
        // Target not yet assigned (RPC_SetTarget hasn't arrived yet) — ignore grip.
        if (CurrentTargetId == null) return;

        Vector3 controllerPos = GetRightControllerWorldPos();
        string nearestId   = null;
        float  nearestDist = k_proximityThreshold;
        foreach (var kvp in _qrManager.DetectedMarkers)
        {
            if (kvp.Key.StartsWith(QR_CALIB_PREFIX)) continue;
            if (kvp.Value == null) continue;
            float dist = Vector3.Distance(controllerPos, kvp.Value.transform.position);
            if (dist < nearestDist) { nearestDist = dist; nearestId = kvp.Key; }
        }

        if (nearestId == null)
        {
            Debug.Log($"[IdentificationTask] Grip: no QR within {k_proximityThreshold * 100:F0} cm.");
            return;
        }

        if (nearestId == CurrentTargetId)
        {
            Debug.Log($"[IdentificationTask] Correct grip on '{nearestId}'!");
            photonView.RPC(nameof(RPC_CorrectHit), RpcTarget.All, nearestId, Score + 1);
        }
        else
        {
            Debug.Log($"[IdentificationTask] Wrong: '{nearestId}' (target='{CurrentTargetId}').");
            photonView.RPC(nameof(RPC_WrongHit), RpcTarget.All, CurrentTargetId, nearestId, Score);
        }
    }

    private Vector3 GetRightControllerWorldPos()
    {
        if (_ovrRig == null) _ovrRig = FindAnyObjectByType<OVRCameraRig>();
        if (_ovrRig != null) return _ovrRig.rightHandAnchor.position;
        return OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
    }
#endif

    [PunRPC]
    private void RPC_CorrectHit(string grippedId, int newScore)
    {
        string prevTarget = CurrentTargetId;
        CompletedMarkerId = grippedId;
        Score             = newScore;
        Debug.Log($"[IdentificationTask] ✓ Correct: gripped='{grippedId}' score={newScore}");
        OnIdentificationAttempt?.Invoke(prevTarget, grippedId, true, newScore);
        OnCorrectGrip?.Invoke();
        // OnTargetChanged fires via RPC_SetTarget when Worker selects the next QR
        if (IsWorker) SelectNextTarget();
    }

    [PunRPC]
    private void RPC_WrongHit(string targetId, string grippedId, int currentScore)
    {
        MissCount++;
        Debug.Log($"[IdentificationTask] ✗ Wrong: '{grippedId}' (target='{targetId}', score={currentScore}, miss={MissCount})");
        OnIdentificationAttempt?.Invoke(targetId, grippedId, false, currentScore);
    }

    private void SetTaskEnabled(bool value)
    {
        enabled = value;
    }
}
