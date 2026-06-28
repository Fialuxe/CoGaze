using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Newtonsoft.Json;
using UnityEngine.InputSystem;

/// <summary>
/// Runs on the Expert PC. Records trial metadata to trials.csv, per-frame gaze+head data
/// to frames.csv, and a per-trial replay JSON file. Also receives Worker hand bone data
/// via Photon event 44 and embeds it in the replay JSON.
/// Added via AddComponent in SceneBootstrapper2.
///
/// CSV schema (trials.csv):
///   trial_id, participant, condition_index, gaze_mode, noise_level,
///   task_type, condition_name,
///   step_type, step_index, start_ms, end_ms, duration_ms
///
/// CSV schema (frames.csv):
///   trial_id, t_ms, elapsed_s, gaze_x, gaze_y, blink,
///   worker_px, worker_py, worker_pz, worker_rx, worker_ry, worker_rz, worker_rw,
///   expert_px, expert_py, expert_pz, expert_rx, expert_ry, expert_rz, expert_rw,
///   osc_certainty
/// </summary>
public class ExperimentLogger : MonoBehaviour, IOnEventCallback
{
    private const byte HAND_EVENT = 44;

    private ExperimentManager2 expManager;
    private int               participantNumber;
    private string            logDir;
    private string            _participantId = "P00";
    private StreamWriter      framesWriter;
    private StreamWriter      escalationsWriter;   // Expert escalation-rung markers (assembly/identification ladder)
    private StreamWriter      _identificationsWriter; // Per-grip attempt rows for identification task

    // Subscribed once when _identTask is first found; unsubscribed in OnDestroy.
    private System.Action<string, string, bool, int> _idAttemptHandler;

    // Per-trial state
    private string                currentTrialId;
    private long                  trialStartMs;
    private int                   trialStepIndex;
    private StepType              trialStepType;
    private int                   trialMaxRung = 1;   // highest escalation rung reached this trial (1 = deictic + gaze only)
    private string                trialTargetMarker = "";   // identification correct-answer target, snapshotted at trial start
    private Vector3               trialMeshPos;
    private Quaternion            trialMeshRot;
    private Vector3               trialMeshScale;
    private List<ReplayFrameData> replayFrames;
    private Coroutine             frameCoroutine;

    // Per-trial extra metadata set via public API before / at trial start
    private string trialTaskType      = "";
    private string trialConditionName = "";

    private const string MESH_NAME = "SharedMesh";

    // Cached component references (searched lazily)
    private GazeHandler        expertGazeHandler;
    private PostureHandler     workerPostureHandler;
    private PostureHandler     expertPostureHandler;
    private IdentificationTask _identTask;
    private VoiceRecorder      _voiceRecorder;
    private int                findAttempts;
    private const int          MAX_FIND_ATTEMPTS = 90; // 3 s at 30 fps

    // Latest hand data received from WorkerHandBroadcaster via event 44
    private float[] latestHandL;
    private float[] latestHandR;
    private int     _frameFlushCounter = 0;

    // Latest OSC certainty value received from /gaze message (mesh_certainty).
    // -1.0 means no value has been received yet in this trial.
    private float latestOscCertainty = -1f;

    // Voice audio offset (legacy voice transport removed — always 0)
    private float             trialVoiceStartSeconds;

    // -------------------------------------------------------------------------
    // Public API — optional setters called before/during a trial
    // -------------------------------------------------------------------------

    /// <summary>
    /// Set the task type label written to trials.csv ("task" or "assembly").
    /// Call before the state transitions to TaskRunning, or omit to leave blank.
    /// </summary>
    public void LogTrialStart(string taskType = "", string conditionName = "")
    {
        trialTaskType      = taskType      ?? "";
        trialConditionName = conditionName ?? "";
    }

    /// <summary>
    /// Push a fresh OSC certainty value so the next CaptureFrame() picks it up.
    /// Corresponds to the mesh_certainty field in the /gaze OSC message.
    /// </summary>
    public void SetOscCertainty(float certainty)
    {
        latestOscCertainty = certainty;
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    public void Initialize(ExperimentManager2 mgr, int participant, string logBaseDirectory = "")
    {
        expManager        = mgr;
        _participantId    = mgr.participantId;
        participantNumber = participant;

        string baseDir = !string.IsNullOrEmpty(logBaseDirectory)
            ? logBaseDirectory
            : Path.Combine(Application.persistentDataPath, "logs");
        logDir = Path.Combine(baseDir, $"P{participant}");
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ExperimentLogger] Cannot create log dir {logDir}: {ex.Message}");
            enabled = false;
            return;
        }

        // Write trials CSV header only if the file is new
        string trialsPath = Path.Combine(logDir, "trials.csv");
        if (!File.Exists(trialsPath))
        {
            try
            {
                File.WriteAllText(trialsPath,
                    "trial_id,participant,condition_index,gaze_mode,noise_level," +
                    "task_type,condition_name," +
                    "step_type,step_index,start_ms,end_ms,duration_ms,identified_marker,target_marker,correct,max_rung,score\n",
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Could not write trials.csv header: {ex.Message}");
            }
        }

        // Open escalations CSV — Expert escalation-rung markers, append across restarts.
        string escalationsPath  = Path.Combine(logDir, "escalations.csv");
        bool   escalationsExist = File.Exists(escalationsPath);
        try
        {
            escalationsWriter = new StreamWriter(escalationsPath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true   // low-frequency operator events — flush immediately so nothing is lost on crash
            };
            if (!escalationsExist)
                escalationsWriter.WriteLine("trial_id,t_ms,elapsed_s,condition_index,task_type,rung");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not open escalations.csv: {ex.Message}");
        }

        // Open identifications CSV — per-grip-attempt rows for the repeated identification task
        string idPath  = Path.Combine(logDir, "identifications.csv");
        bool   idExists = File.Exists(idPath);
        try
        {
            _identificationsWriter = new StreamWriter(idPath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };
            if (!idExists)
                _identificationsWriter.WriteLine("trial_id,t_ms,elapsed_s,target_id,gripped_id,correct,score_after");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not open identifications.csv: {ex.Message}");
        }

        // Open frames CSV — append across session restarts
        string framesPath  = Path.Combine(logDir, "frames.csv");
        bool   framesExist = File.Exists(framesPath);
        try
        {
            framesWriter = new StreamWriter(framesPath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = false
            };
            if (!framesExist)
                framesWriter.WriteLine(
                    "trial_id,t_ms,elapsed_s,gaze_x,gaze_y,blink," +
                    "worker_px,worker_py,worker_pz,worker_rx,worker_ry,worker_rz,worker_rw," +
                    "expert_px,expert_py,expert_pz,expert_rx,expert_ry,expert_rz,expert_rw," +
                    "osc_certainty,ctrl_px,ctrl_py,ctrl_pz");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not open frames.csv: {ex.Message}");
        }

        PhotonNetwork.AddCallbackTarget(this);
        expManager.OnStateChanged += OnStateChanged;

        FileLogger.Log("ExperimentLogger", $"Initialized. Logs → {logDir}");
    }

    // -------------------------------------------------------------------------
    // State machine
    // -------------------------------------------------------------------------

    private void OnStateChanged(ExperimentState state)
    {
        if (state == ExperimentState.TaskRunning)
        {
            BeginTrial();
        }
        else if (currentTrialId != null)
        {
            EndTrial();
        }
    }

    private void BeginTrial()
    {
        currentTrialId   = Guid.NewGuid().ToString("N").Substring(0, 8);
        trialStartMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        trialStepIndex   = expManager.CurrentStepIndex;
        trialStepType    = expManager.CurrentStepType;
        trialMaxRung     = 1;   // reset the escalation ladder for the new trial
        // Snapshot the target NOW: completion advances the step before the end-row is written, so
        // reading it at trial end would capture the next step's (empty) target.
        trialTargetMarker = expManager.CurrentTargetMarkerId ?? "";

        // Auto-populate fields that LogTrialStart() was supposed to set but was never called.
        trialTaskType = trialStepType == StepType.Task     ? "task"
                      : trialStepType == StepType.Assembly ? "assembly"
                      : trialStepType.ToString().ToLowerInvariant();
        int ci = expManager.CurrentConditionIndex;
        if (ci >= 0)
        {
            var (gaze, noise) = expManager.GetConditionInfo(ci);
            trialConditionName = $"{gaze}_{noise}";
        }

        replayFrames     = new List<ReplayFrameData>();
        latestHandL      = null;
        latestHandR      = null;
        latestOscCertainty = -1f;
        findAttempts     = 0;
        if (_identTask == null)
            _identTask = FindAnyObjectByType<IdentificationTask>();

        // Subscribe to per-grip attempts once (handler persists across trials; null-guard prevents double-subscribe)
        if (_identTask != null && _idAttemptHandler == null)
        {
            _idAttemptHandler = (targetId, grippedId, correct, scoreAfter) =>
            {
                if (currentTrialId == null || trialStepType != StepType.Task) return;
                long   nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                float  elapsed = (nowMs - trialStartMs) / 1000f;
                try
                {
                    _identificationsWriter?.WriteLine(
                        $"{currentTrialId},{nowMs},{elapsed:F3},{targetId},{grippedId},{(correct ? "1" : "0")},{scoreAfter}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ExperimentLogger] identifications.csv write error: {ex.Message}");
                }
            };
            _identTask.OnIdentificationAttempt += _idAttemptHandler;
        }

        if (_voiceRecorder == null)
            _voiceRecorder = GetComponent<VoiceRecorder>();
        // Legacy per-trial voice-align transport was removed; this offset is intentionally always 0
        // (used as replay voiceStartSeconds). Single explicit assignment — was a dead double-assign.
        trialVoiceStartSeconds = 0f;

        // Snapshot mesh transform at trial start
        var meshObj = GameObject.Find(MESH_NAME);
        if (meshObj != null)
        {
            trialMeshPos   = meshObj.transform.position;
            trialMeshRot   = meshObj.transform.rotation;
            trialMeshScale = meshObj.transform.localScale;
        }
        else
        {
            trialMeshPos   = Vector3.zero;
            trialMeshRot   = Quaternion.identity;
            trialMeshScale = Vector3.one;
            Debug.LogWarning($"[ExperimentLogger] '{MESH_NAME}' not found — mesh transform will be zero in replay.");
        }

        if (frameCoroutine != null) StopCoroutine(frameCoroutine);
        frameCoroutine = StartCoroutine(FrameLoop());

        FileLogger.Log("ExperimentLogger", $"Trial started: id={currentTrialId} step={trialStepIndex} type={trialStepType} taskType={trialTaskType} conditionName={trialConditionName}");
    }

    private void EndTrial()
    {
        if (frameCoroutine != null)
        {
            StopCoroutine(frameCoroutine);
            frameCoroutine = null;
        }

        long endMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long durationMs = endMs - trialStartMs;

        int condIdx = expManager.CurrentConditionIndex;
        var (gazeMode, noiseLevel) = expManager.GetConditionInfo(condIdx);

        // Flush frame data to disk
        try { framesWriter?.Flush(); }
        catch (Exception ex) { Debug.LogWarning($"[ExperimentLogger] Flush error: {ex.Message}"); }

        // Append trial row — includes task_type and condition_name
        string trialsPath   = Path.Combine(logDir, "trials.csv");
        // Only the identification (Task) trial selects a marker, and CompletedMarkerId is not cleared at
        // task end — so writing it on an Assembly row would record a STALE identification marker. Blank it
        // for non-Task steps so Assembly rows carry no misleading identified_marker.
        string markerResult = (trialStepType == StepType.Task) ? (_identTask?.CompletedMarkerId ?? "") : "";
        // correct: blank when untracked (no target), else 1/0 on exact match of the Worker's selection.
        string correct = string.IsNullOrEmpty(trialTargetMarker) ? ""
                       : (markerResult == trialTargetMarker ? "1" : "0");
        string scoreStr = (trialStepType == StepType.Task && _identTask != null)
            ? _identTask.Score.ToString() : "";
        string row = $"{currentTrialId},{_participantId},{condIdx},{gazeMode},{noiseLevel}," +
                     $"{trialTaskType},{trialConditionName}," +
                     $"{trialStepType},{trialStepIndex},{trialStartMs},{endMs},{durationMs}," +
                     $"{markerResult},{trialTargetMarker},{correct},{trialMaxRung},{scoreStr}\n";
        try
        {
            File.AppendAllText(trialsPath, row, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Trial row write failed: {ex.Message}");
        }

        // Serialize replay JSON
        SaveReplayJson(condIdx, gazeMode.ToString(), noiseLevel);

        FileLogger.Log("ExperimentLogger", $"Trial ended: id={currentTrialId} frames={replayFrames?.Count} duration={durationMs}ms");
        currentTrialId     = null;
        replayFrames       = null;
        trialTaskType      = "";
        trialConditionName = "";
    }

    // -------------------------------------------------------------------------
    // Escalation-rung markers (Expert keyboard, during an active trial)
    // -------------------------------------------------------------------------
    //
    // The Expert climbs a graduated verbal ladder when gaze alone is not getting the
    // instruction across: rung 1 = deictic + gaze, rung 2 = feature/relative words,
    // rung 3 = full spatial/coordinate, rung 4 = orientation (assembly only). They mark
    // the rung reached in real time with F2/F3/F4 so "how far the Expert had to climb" is
    // captured per trial (max_rung in trials.csv) plus a timestamped event stream
    // (escalations.csv). Active only while a Task/Assembly trial is running; on the Worker
    // there is no keyboard so this is inert.

    private void Update()
    {
        if (currentTrialId == null) return;     // only during an active trial (Expert-side)
        var kb = Keyboard.current;
        if (kb == null) return;                 // no keyboard (e.g. Worker/Quest) — nothing to mark

        if (kb.f2Key.wasPressedThisFrame) RecordEscalation(2);
        if (kb.f3Key.wasPressedThisFrame) RecordEscalation(3);
        // rung4 = orientation help, which only exists in the assembly task. Gate F4 to Assembly so a
        // stray press during the 60s identification trial cannot pollute max_rung / escalations.csv (ESCAL-01).
        if (kb.f4Key.wasPressedThisFrame && trialStepType == StepType.Assembly) RecordEscalation(4);
    }

    private void RecordEscalation(int rung)
    {
        if (rung > trialMaxRung) trialMaxRung = rung;

        long  nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float elapsed = (nowMs - trialStartMs) / 1000f;
        int   condIdx = expManager != null ? expManager.CurrentConditionIndex : -1;

        if (escalationsWriter != null)
        {
            try
            {
                escalationsWriter.WriteLine($"{currentTrialId},{nowMs},{elapsed:F3},{condIdx},{trialTaskType},{rung}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Escalation write error: {ex.Message}");
            }
        }
        FileLogger.Log("ExperimentLogger", $"Escalation rung {rung} (max={trialMaxRung}) trial={currentTrialId} task={trialTaskType}");
    }

    private void SaveReplayJson(int condIdx, string gazeMode, string noiseLevel)
    {
        if (replayFrames == null || replayFrames.Count == 0) return;

        var data = new ReplayData
        {
            meta = new ReplayMeta
            {
                participantNumber = participantNumber,
                trialId           = currentTrialId,
                conditionIndex    = condIdx,
                gazeMode          = gazeMode,
                noiseLevel        = noiseLevel,
                stepType          = trialStepType.ToString(),
                stepIndex         = trialStepIndex,
                startMs           = trialStartMs,
                meshPos   = new[] { trialMeshPos.x,   trialMeshPos.y,   trialMeshPos.z },
                meshRot   = new[] { trialMeshRot.x,   trialMeshRot.y,   trialMeshRot.z,   trialMeshRot.w },
                meshScale = new[] { trialMeshScale.x, trialMeshScale.y, trialMeshScale.z },
                voiceWavPath      = _voiceRecorder?.LocalWavPath,
                voiceStartSeconds = trialVoiceStartSeconds
            },
            frames = replayFrames
        };

        string path = Path.Combine(logDir, $"replay_{currentTrialId}.json");
        try
        {
            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            File.WriteAllText(path, JsonConvert.SerializeObject(data, settings), Encoding.UTF8);
            FileLogger.Log("ExperimentLogger", $"Replay → {path}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Replay JSON write failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Frame capture
    // -------------------------------------------------------------------------

    private IEnumerator FrameLoop()
    {
        var wait = new WaitForSecondsRealtime(1f / 30f);
        while (true)
        {
            yield return wait;
            try { CaptureFrame(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] CaptureFrame error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Capture one frame to CSV and replay buffer.
    /// oscCertainty: optional override for this specific frame's certainty value.
    /// When omitted, uses the last value set via SetOscCertainty() (default -1).
    /// </summary>
    private void CaptureFrame(float oscCertainty = float.NaN)
    {
        // Lazy component search with retry cap
        if (expertGazeHandler == null && findAttempts < MAX_FIND_ATTEMPTS)
        {
            foreach (var gh in FindObjectsByType<GazeHandler>(FindObjectsSortMode.None))
            {
                if (gh.photonView.IsMine)
                {
                    expertGazeHandler    = gh;
                    expertPostureHandler = gh.GetComponent<PostureHandler>();
                    break;
                }
            }
            findAttempts++;
            if (expertGazeHandler == null && findAttempts >= MAX_FIND_ATTEMPTS)
                Debug.LogWarning("[ExperimentLogger] Expert GazeHandler not found after retries — logging zeros.");
        }

        if (workerPostureHandler == null)
        {
            foreach (var ph in FindObjectsByType<PostureHandler>(FindObjectsSortMode.None))
            {
                if (!ph.photonView.IsMine) { workerPostureHandler = ph; break; }
            }
        }

        long  nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float elapsed = (nowMs - trialStartMs) / 1000f;

        Vector3    gaze       = expertGazeHandler    != null ? expertGazeHandler.CurrentGazeData         : Vector3.zero;
        Vector3    workerPos  = workerPostureHandler != null ? workerPostureHandler.transform.position    : Vector3.zero;
        Quaternion workerRot  = workerPostureHandler != null ? workerPostureHandler.transform.rotation    : Quaternion.identity;
        Vector3    expertPos  = expertPostureHandler != null ? expertPostureHandler.transform.position    : Vector3.zero;
        Quaternion expertRot  = expertPostureHandler != null ? expertPostureHandler.transform.rotation    : Quaternion.identity;
        bool       hasCtrl    = WorkerTrackingReader.TryGetControllerPosition(out Vector3 ctrlPos);

        // Resolve certainty: prefer per-frame override, fall back to latest pushed value
        float certainty = float.IsNaN(oscCertainty) ? latestOscCertainty : oscCertainty;

        // CSV row — osc_certainty appended as last column
        if (framesWriter != null)
        {
            try
            {
                framesWriter.WriteLine(
                    $"{currentTrialId},{nowMs},{elapsed:F3}," +
                    $"{gaze.x:F4},{gaze.y:F4},{gaze.z:F4}," +
                    $"{workerPos.x:F4},{workerPos.y:F4},{workerPos.z:F4}," +
                    $"{workerRot.x:F4},{workerRot.y:F4},{workerRot.z:F4},{workerRot.w:F4}," +
                    $"{expertPos.x:F4},{expertPos.y:F4},{expertPos.z:F4}," +
                    $"{expertRot.x:F4},{expertRot.y:F4},{expertRot.z:F4},{expertRot.w:F4}," +
                    $"{certainty:F4}," +
                    $"{ctrlPos.x:F4},{ctrlPos.y:F4},{ctrlPos.z:F4}");
                if (++_frameFlushCounter >= 30)
                {
                    framesWriter.Flush();
                    _frameFlushCounter = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Frame CSV write error: {ex.Message}");
            }
        }

        // Replay frame
        if (replayFrames != null)
        {
            replayFrames.Add(new ReplayFrameData
            {
                t          = elapsed,
                gaze       = new[] { gaze.x, gaze.y, gaze.z },
                workerHead = new ReplayHeadPose
                {
                    p = new[] { workerPos.x, workerPos.y, workerPos.z },
                    r = new[] { workerRot.x, workerRot.y, workerRot.z, workerRot.w }
                },
                expertHead = new ReplayHeadPose
                {
                    p = new[] { expertPos.x, expertPos.y, expertPos.z },
                    r = new[] { expertRot.x, expertRot.y, expertRot.z, expertRot.w }
                },
                handL      = UnpackBones(latestHandL),
                handR      = UnpackBones(latestHandR),
                workerCtrl = hasCtrl ? new[] { ctrlPos.x, ctrlPos.y, ctrlPos.z } : null
            });
        }
    }

    private static float[][] UnpackBones(float[] flat)
    {
        if (flat == null || flat.Length < 72) return null;
        var result = new float[24][];
        for (int i = 0; i < 24; i++)
            result[i] = new[] { flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2] };
        return result;
    }

    // -------------------------------------------------------------------------
    // Photon events
    // -------------------------------------------------------------------------

    public void OnEvent(EventData ev)
    {
        if (ev.Code != HAND_EVENT) return;
        try
        {
            var arr = ev.CustomData as object[];
            if (arr == null || arr.Length < 2)
            {
                Debug.LogWarning("[ExperimentLogger] Hand event: unexpected data shape.");
                return;
            }
            var l = arr[0] as float[];
            var r = arr[1] as float[];
            if (l == null || r == null)
            {
                Debug.LogWarning("[ExperimentLogger] Hand event: payload is not float[].");
                return;
            }
            latestHandL = l.Length >= 72 ? l : null;
            latestHandR = r.Length >= 72 ? r : null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Hand event parse error: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
        if (expManager != null) expManager.OnStateChanged -= OnStateChanged;

        if (frameCoroutine != null) StopCoroutine(frameCoroutine);

        // Flush any in-progress trial so data is not lost on crash or scene reload
        if (currentTrialId != null)
        {
            try { EndTrial(); }
            catch (Exception ex) { Debug.LogWarning($"[ExperimentLogger] EndTrial on destroy error: {ex.Message}"); }
        }

        try
        {
            framesWriter?.Flush();
            framesWriter?.Close();
            framesWriter = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] StreamWriter close error: {ex.Message}");
        }

        try
        {
            escalationsWriter?.Flush();
            escalationsWriter?.Close();
            escalationsWriter = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] escalations.csv close error: {ex.Message}");
        }

        try
        {
            _identificationsWriter?.Flush();
            _identificationsWriter?.Close();
            _identificationsWriter = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] identifications.csv close error: {ex.Message}");
        }

        if (_identTask != null && _idAttemptHandler != null)
        {
            _identTask.OnIdentificationAttempt -= _idAttemptHandler;
            _idAttemptHandler = null;
        }
    }
}
