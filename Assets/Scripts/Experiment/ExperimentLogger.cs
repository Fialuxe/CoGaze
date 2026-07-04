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

// Expert-side logger: writes trials.csv (metadata), frames.csv (per-frame gaze+head), and per-trial replay JSON.
public class ExperimentLogger : MonoBehaviour, IOnEventCallback
{
    private const byte k_handEvent = 44;

    private ExperimentManager2 _expManager;
    private int               _participantNumber;
    private string            _logDir;
    private string            _participantId = "P00";
    private StreamWriter      _framesWriter;
    private StreamWriter      _escalationsWriter;   // Expert escalation-rung markers (assembly/identification ladder)
    private StreamWriter      _identificationsWriter; // Per-grip attempt rows for identification task

    // Subscribed once when _identTask is first found; unsubscribed in OnDestroy.
    private System.Action<string, string, bool, int> _idAttemptHandler;

    // Per-trial state
    private string                _currentTrialId;
    private long                  _trialStartMs;
    private int                   _trialStepIndex;
    private StepType              _trialStepType;
    private int                   _trialMaxRung = 1;   // highest escalation rung reached this trial (1 = deictic + gaze only)
    private int                   _trialAttempts;      // identification grip attempts this trial (Task steps only)
    private int                   _trialRunPos = -1;   // 0-based presentation position, snapshotted at trial start
    private Vector3               _trialMeshPos;
    private Quaternion            _trialMeshRot;
    private Vector3               _trialMeshScale;
    private List<ReplayFrameData> _replayFrames;
    private Coroutine             _frameCoroutine;

    // Per-trial extra metadata set via public API before / at trial start
    private string _trialTaskType      = "";
    private string _trialConditionName = "";

    private const string k_meshName = "SharedMesh";

    // Cached component references (searched lazily)
    private GazeHandler        _expertGazeHandler;
    private PostureHandler     _workerPostureHandler;
    private PostureHandler     _expertPostureHandler;
    private IdentificationTask _identTask;
    private VoiceRecorder      _voiceRecorder;
    private int                _findAttempts;
    private const int          k_maxFindAttempts = 90; // 3 s at 30 fps

    // Latest hand data received from WorkerHandBroadcaster via event 44
    private float[] _latestHandL;
    private float[] _latestHandR;
    private int     _frameFlushCounter;

    // Latest OSC certainty value received from /gaze message (mesh_certainty).
    // -1.0 means no value has been received yet in this trial.
    private float _latestOscCertainty = -1f;

    // calibrations.csv — per-attempt calibration events (incl. retries), FK condition_index.
    private string _calibrationsPath;

    // Voice audio offset (legacy voice transport removed — always 0)
    private float             _trialVoiceStartSeconds;

    // -------------------------------------------------------------------------
    // Public API — optional setters called before/during a trial
    // -------------------------------------------------------------------------

    public void LogTrialStart(string taskType = "", string conditionName = "")
    {
        _trialTaskType      = taskType      ?? "";
        _trialConditionName = conditionName ?? "";
    }

    public void SetOscCertainty(float certainty)
    {
        _latestOscCertainty = certainty;
    }

    // Append a calibration attempt (incl. retries) to calibrations.csv. Keyed by the condition it
    // calibrated, not by trial — calibration runs between trials at ConditionStart.
    public void SetCalibResult(int quality, float errX, float errY)
    {
        if (string.IsNullOrEmpty(_calibrationsPath)) return;   // before Initialize
        long nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int  condIdx = _expManager != null ? _expManager.CurrentConditionIndex : -1;
        try
        {
            File.AppendAllText(_calibrationsPath,
                $"{nowMs},{condIdx},{quality},{errX:F3},{errY:F3}\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] calibrations.csv write error: {ex.Message}");
        }
    }

    // Header-versioned CSV create: if an existing file's header differs from the current schema
    // (file written by an older build), move it aside as *.old-<timestamp>.csv so new rows are
    // never appended under a stale header; then create the file with the header if missing.
    private static void EnsureCsvSchema(string path, string header)
    {
        try
        {
            if (File.Exists(path))
            {
                string firstLine;
                using (var sr = new StreamReader(path, Encoding.UTF8))
                    firstLine = sr.ReadLine();
                if (firstLine == header) return;
                string backup = Path.Combine(
                    Path.GetDirectoryName(path) ?? "",
                    $"{Path.GetFileNameWithoutExtension(path)}.old-{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(path)}");
                File.Move(path, backup);
                Debug.LogWarning($"[ExperimentLogger] Schema changed for {Path.GetFileName(path)} — old file moved to {Path.GetFileName(backup)}.");
            }
            File.WriteAllText(path, header + "\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] EnsureCsvSchema({Path.GetFileName(path)}) failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    public void Initialize(ExperimentManager2 mgr, int participant, string logBaseDirectory = "")
    {
        _expManager        = mgr;
        _participantId    = mgr.participantId;
        _participantNumber = participant;

        string baseDir = !string.IsNullOrEmpty(logBaseDirectory)
            ? logBaseDirectory
            : Path.Combine(Application.persistentDataPath, "logs");
        _logDir = Path.Combine(baseDir, $"P{participant}");
        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ExperimentLogger] Cannot create log dir {_logDir}: {ex.Message}");
            enabled = false;
            return;
        }

        // ── Relational CSV layout ────────────────────────────────────────────
        // conditions.csv   = static dimension table (condition_index → name/tracking/gaze).
        // sessions.csv     = one row per app run (participant attrs + resume offset live here).
        // trials.csv       = per-trial facts; PK trial_id, FK condition_index.
        // identifications / escalations / frames = per-event / stream facts; FK trial_id.
        // calibrations.csv = per-attempt calibration events; FK condition_index.
        // Same-row derivables (duration_ms) and condition-name denormalizations are dropped, as
        // are the legacy single-shot identification columns (identified/target/correct) — the
        // task is repeated identification, so per-grip ground truth lives in identifications.csv.
        // EnsureCsvSchema moves any old-schema file aside so rows never append under a stale header.

        // conditions.csv — dimension table, written once
        string conditionsPath = Path.Combine(_logDir, "conditions.csv");
        if (!File.Exists(conditionsPath))
        {
            try
            {
                var sb = new StringBuilder("condition_index,condition_name,tracking,gaze_mode\n");
                for (int i = 0; i < ExperimentDesign.Conditions.Length; i++)
                {
                    var c = ExperimentDesign.Conditions[i];
                    sb.Append($"{i},{c.name},{c.noise.ToString().ToLowerInvariant()},{c.gaze.ToString().ToLowerInvariant()}\n");
                }
                File.WriteAllText(conditionsPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Could not write conditions.csv: {ex.Message}");
            }
        }

        // sessions.csv — one row per app run (documents resumes via start_condition_offset)
        string sessionsPath = Path.Combine(_logDir, "sessions.csv");
        EnsureCsvSchema(sessionsPath, "session_start_ms,participant,order_index,start_condition_offset");
        try
        {
            File.AppendAllText(sessionsPath,
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{_participantId},{mgr.participantOrderIndex},{mgr.startConditionOffset}\n",
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not append sessions.csv: {ex.Message}");
        }

        string trialsPath = Path.Combine(_logDir, "trials.csv");
        EnsureCsvSchema(trialsPath,
            "trial_id,participant,run_pos,condition_index,step_type,step_index,start_ms,end_ms,attempts,score,max_rung");

        _calibrationsPath = Path.Combine(_logDir, "calibrations.csv");
        EnsureCsvSchema(_calibrationsPath, "t_ms,condition_index,quality,err_x,err_y");

        // Open escalations CSV — Expert escalation-rung markers, append across restarts.
        // condition/task columns were dropped — join via trial_id → trials.csv.
        string escalationsPath = Path.Combine(_logDir, "escalations.csv");
        EnsureCsvSchema(escalationsPath, "trial_id,t_ms,elapsed_s,rung");
        try
        {
            _escalationsWriter = new StreamWriter(escalationsPath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true   // low-frequency operator events — flush immediately so nothing is lost on crash
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not open escalations.csv: {ex.Message}");
        }

        // Open identifications CSV — per-grip-attempt rows for the repeated identification task
        string idPath = Path.Combine(_logDir, "identifications.csv");
        EnsureCsvSchema(idPath, "trial_id,t_ms,elapsed_s,target_id,gripped_id,correct,score_after");
        try
        {
            _identificationsWriter = new StreamWriter(idPath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not open identifications.csv: {ex.Message}");
        }

        // Open frames CSV — append across session restarts
        string framesPath = Path.Combine(_logDir, "frames.csv");
        EnsureCsvSchema(framesPath,
            "trial_id,t_ms,elapsed_s,gaze_x,gaze_y,blink," +
            "worker_px,worker_py,worker_pz,worker_rx,worker_ry,worker_rz,worker_rw," +
            "expert_px,expert_py,expert_pz,expert_rx,expert_ry,expert_rz,expert_rw," +
            "osc_certainty,ctrl_px,ctrl_py,ctrl_pz");
        try
        {
            _framesWriter = new StreamWriter(framesPath, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = false
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] Could not open frames.csv: {ex.Message}");
        }

        PhotonNetwork.AddCallbackTarget(this);
        _expManager.OnStateChanged += OnStateChanged;

        FileLogger.Log("ExperimentLogger", $"Initialized. Logs → {_logDir}");
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
        else if (_currentTrialId != null)
        {
            EndTrial();
        }
    }

    private void BeginTrial()
    {
        _currentTrialId   = Guid.NewGuid().ToString("N").Substring(0, 8);
        _trialStartMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _trialStepIndex   = _expManager.CurrentStepIndex;
        _trialStepType    = _expManager.CurrentStepType;
        _trialMaxRung     = 1;   // reset the escalation ladder for the new trial
        _trialAttempts    = 0;
        // Snapshot NOW: completion advances the step before the end-row is written.
        _trialRunPos      = _expManager.CurrentConditionRunPosition;

        // Auto-populate fields that LogTrialStart() was supposed to set but was never called.
        _trialTaskType = _trialStepType == StepType.Task     ? "task"
                      : _trialStepType == StepType.Assembly ? "assembly"
                      : _trialStepType.ToString().ToLowerInvariant();
        int ci = _expManager.CurrentConditionIndex;
        if (ci >= 0)
        {
            var (gaze, noise) = _expManager.GetConditionInfo(ci);
            _trialConditionName = $"{gaze}_{noise}";
        }

        _replayFrames     = new List<ReplayFrameData>();
        _latestHandL      = null;
        _latestHandR      = null;
        _latestOscCertainty = -1f;
        _findAttempts     = 0;
        if (_identTask == null)
            _identTask = FindAnyObjectByType<IdentificationTask>();

        // Subscribe to per-grip attempts once (handler persists across trials; null-guard prevents double-subscribe)
        if (_identTask != null && _idAttemptHandler == null)
        {
            _idAttemptHandler = (targetId, grippedId, correct, scoreAfter) =>
            {
                if (_currentTrialId == null || _trialStepType != StepType.Task) return;
                _trialAttempts++;
                long   nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                float  elapsed = (nowMs - _trialStartMs) / 1000f;
                try
                {
                    _identificationsWriter?.WriteLine(
                        $"{_currentTrialId},{nowMs},{elapsed:F3},{targetId},{grippedId},{(correct ? "1" : "0")},{scoreAfter}");
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
        _trialVoiceStartSeconds = 0f;

        // Snapshot mesh transform at trial start
        var meshObj = GameObject.Find(k_meshName);
        if (meshObj != null)
        {
            _trialMeshPos   = meshObj.transform.position;
            _trialMeshRot   = meshObj.transform.rotation;
            _trialMeshScale = meshObj.transform.localScale;
        }
        else
        {
            _trialMeshPos   = Vector3.zero;
            _trialMeshRot   = Quaternion.identity;
            _trialMeshScale = Vector3.one;
            Debug.LogWarning($"[ExperimentLogger] '{k_meshName}' not found — mesh transform will be zero in replay.");
        }

        if (_frameCoroutine != null) StopCoroutine(_frameCoroutine);
        _frameCoroutine = StartCoroutine(FrameLoop());

        FileLogger.Log("ExperimentLogger", $"Trial started: id={_currentTrialId} step={_trialStepIndex} type={_trialStepType} taskType={_trialTaskType} conditionName={_trialConditionName}");
    }

    private void EndTrial()
    {
        if (_frameCoroutine != null)
        {
            StopCoroutine(_frameCoroutine);
            _frameCoroutine = null;
        }

        long endMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long durationMs = endMs - _trialStartMs;

        int condIdx = _expManager.CurrentConditionIndex;
        var (gazeMode, noiseLevel) = _expManager.GetConditionInfo(condIdx);

        // Flush frame data to disk
        try { _framesWriter?.Flush(); }
        catch (Exception ex) { Debug.LogWarning($"[ExperimentLogger] Flush error: {ex.Message}"); }

        // Append trial row. Identification accuracy is summarized only as attempts/score — the
        // task is repeated identification with dynamic targets, so per-grip ground truth
        // (target_id, gripped_id, correct) lives in identifications.csv keyed by trial_id.
        string trialsPath  = Path.Combine(_logDir, "trials.csv");
        bool   isTask      = _trialStepType == StepType.Task;
        string runPosStr   = _trialRunPos >= 0 ? (_trialRunPos + 1).ToString() : "";
        string attemptsStr = isTask ? _trialAttempts.ToString() : "";
        string scoreStr    = (isTask && _identTask != null) ? _identTask.Score.ToString() : "";
        string row = $"{_currentTrialId},{_participantId},{runPosStr},{condIdx}," +
                     $"{_trialStepType},{_trialStepIndex},{_trialStartMs},{endMs}," +
                     $"{attemptsStr},{scoreStr},{_trialMaxRung}\n";
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

        FileLogger.Log("ExperimentLogger", $"Trial ended: id={_currentTrialId} frames={_replayFrames?.Count} duration={durationMs}ms");
        _currentTrialId     = null;
        _replayFrames       = null;
        _trialTaskType      = "";
        _trialConditionName = "";
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
        if (_currentTrialId == null) return;     // only during an active trial (Expert-side)
        var kb = Keyboard.current;
        if (kb == null) return;                 // no keyboard (e.g. Worker/Quest) — nothing to mark

        if (kb.f2Key.wasPressedThisFrame) RecordEscalation(2);
        if (kb.f3Key.wasPressedThisFrame) RecordEscalation(3);
        // rung4 = orientation help, which only exists in the assembly task. Gate F4 to Assembly so a
        // stray press during the 60s identification trial cannot pollute max_rung / escalations.csv (ESCAL-01).
        if (kb.f4Key.wasPressedThisFrame && _trialStepType == StepType.Assembly) RecordEscalation(4);
    }

    private void RecordEscalation(int rung)
    {
        if (rung > _trialMaxRung) _trialMaxRung = rung;

        long  nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float elapsed = (nowMs - _trialStartMs) / 1000f;

        if (_escalationsWriter != null)
        {
            try
            {
                _escalationsWriter.WriteLine($"{_currentTrialId},{nowMs},{elapsed:F3},{rung}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Escalation write error: {ex.Message}");
            }
        }
        FileLogger.Log("ExperimentLogger", $"Escalation rung {rung} (max={_trialMaxRung}) trial={_currentTrialId} task={_trialTaskType}");
    }

    private void SaveReplayJson(int condIdx, string gazeMode, string noiseLevel)
    {
        if (_replayFrames == null || _replayFrames.Count == 0) return;

        var data = new ReplayData
        {
            meta = new ReplayMeta
            {
                participantNumber  = _participantNumber,
                trialId           = _currentTrialId,
                conditionIndex    = condIdx,
                gazeMode          = gazeMode,
                noiseLevel        = noiseLevel,
                stepType          = _trialStepType.ToString(),
                stepIndex         = _trialStepIndex,
                startMs           = _trialStartMs,
                meshPos   = new[] { _trialMeshPos.x,   _trialMeshPos.y,   _trialMeshPos.z },
                meshRot   = new[] { _trialMeshRot.x,   _trialMeshRot.y,   _trialMeshRot.z,   _trialMeshRot.w },
                meshScale = new[] { _trialMeshScale.x, _trialMeshScale.y, _trialMeshScale.z },
                voiceWavPath      = _voiceRecorder?.LocalWavPath,
                voiceStartSeconds = _trialVoiceStartSeconds
            },
            frames = _replayFrames
        };

        string path = Path.Combine(_logDir, $"replay_{_currentTrialId}.json");
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

    private void CaptureFrame(float oscCertainty = float.NaN)
    {
        // Lazy component search with retry cap
        if (_expertGazeHandler == null && _findAttempts < k_maxFindAttempts)
        {
            foreach (var gh in FindObjectsByType<GazeHandler>(FindObjectsSortMode.None))
            {
                if (gh.photonView.IsMine)
                {
                    _expertGazeHandler    = gh;
                    _expertPostureHandler = gh.GetComponent<PostureHandler>();
                    break;
                }
            }
            _findAttempts++;
            if (_expertGazeHandler == null && _findAttempts >= k_maxFindAttempts)
                Debug.LogWarning("[ExperimentLogger] Expert GazeHandler not found after retries — logging zeros.");
        }

        if (_workerPostureHandler == null)
        {
            foreach (var ph in FindObjectsByType<PostureHandler>(FindObjectsSortMode.None))
            {
                if (!ph.photonView.IsMine) { _workerPostureHandler = ph; break; }
            }
        }

        long  nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float elapsed = (nowMs - _trialStartMs) / 1000f;

        Vector3    gaze       = _expertGazeHandler    != null ? _expertGazeHandler.CurrentGazeData         : Vector3.zero;
        Vector3    workerPos  = _workerPostureHandler != null ? _workerPostureHandler.transform.position    : Vector3.zero;
        Quaternion workerRot  = _workerPostureHandler != null ? _workerPostureHandler.transform.rotation    : Quaternion.identity;
        Vector3    expertPos  = _expertPostureHandler != null ? _expertPostureHandler.transform.position    : Vector3.zero;
        Quaternion expertRot  = _expertPostureHandler != null ? _expertPostureHandler.transform.rotation    : Quaternion.identity;
        bool       hasCtrl    = WorkerTrackingReader.TryGetControllerPosition(out Vector3 ctrlPos);

        // Resolve certainty: prefer per-frame override, fall back to latest pushed value
        float certainty = float.IsNaN(oscCertainty) ? _latestOscCertainty : oscCertainty;

        // CSV row — osc_certainty appended as last column
        if (_framesWriter != null)
        {
            try
            {
                _framesWriter.WriteLine(
                    $"{_currentTrialId},{nowMs},{elapsed:F3}," +
                    $"{gaze.x:F4},{gaze.y:F4},{gaze.z:F4}," +
                    $"{workerPos.x:F4},{workerPos.y:F4},{workerPos.z:F4}," +
                    $"{workerRot.x:F4},{workerRot.y:F4},{workerRot.z:F4},{workerRot.w:F4}," +
                    $"{expertPos.x:F4},{expertPos.y:F4},{expertPos.z:F4}," +
                    $"{expertRot.x:F4},{expertRot.y:F4},{expertRot.z:F4},{expertRot.w:F4}," +
                    $"{certainty:F4}," +
                    $"{ctrlPos.x:F4},{ctrlPos.y:F4},{ctrlPos.z:F4}");
                if (++_frameFlushCounter >= 30)
                {
                    _framesWriter.Flush();
                    _frameFlushCounter = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ExperimentLogger] Frame CSV write error: {ex.Message}");
            }
        }

        // Replay frame
        if (_replayFrames != null)
        {
            _replayFrames.Add(new ReplayFrameData
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
                handL      = UnpackBones(_latestHandL),
                handR      = UnpackBones(_latestHandR),
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
        if (ev.Code != k_handEvent) return;
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
            _latestHandL = l.Length >= 72 ? l : null;
            _latestHandR = r.Length >= 72 ? r : null;
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
        if (_expManager != null) _expManager.OnStateChanged -= OnStateChanged;

        if (_frameCoroutine != null) StopCoroutine(_frameCoroutine);

        // Flush any in-progress trial so data is not lost on crash or scene reload
        if (_currentTrialId != null)
        {
            try { EndTrial(); }
            catch (Exception ex) { Debug.LogWarning($"[ExperimentLogger] EndTrial on destroy error: {ex.Message}"); }
        }

        try
        {
            _framesWriter?.Flush();
            _framesWriter?.Close();
            _framesWriter = null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ExperimentLogger] StreamWriter close error: {ex.Message}");
        }

        try
        {
            _escalationsWriter?.Flush();
            _escalationsWriter?.Close();
            _escalationsWriter = null;
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
