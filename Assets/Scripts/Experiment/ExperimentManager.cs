using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
#if !UNITY_ANDROID
using System.Diagnostics;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

// ── Enums / Data ──────────────────────────────────────────────────────────────

public enum StepType : byte
{
    Noise          = 0,
    Task           = 1,
    Questionnaire  = 2,
    Assembly       = 3,
    Alignment      = 4,  // position-alignment gate — video feed ON, no timer, Enter to advance
    ConditionStart = 5,  // auto-generated: switches gaze mode + launches Python, then questionnaire gate
}

public enum ExperimentState : byte
{
    Idle          = 0,
    Ready         = 1,
    WhiteNoise    = 2,
    TaskRunning   = 3,
    Questionnaire = 4,
    Finished      = 5,
    TaskComplete  = 6,
    NoiseComplete = 7
}

public class ExperimentStep
{
    public StepType Type;
    public string   Instruction  = string.Empty;
    public int      ConditionIndex = -1; // only set for ConditionStart steps
}

// ── ExperimentManager ─────────────────────────────────────────────────────────

/// <summary>
/// Drives a 9-condition experiment (3 gaze modes × 3 noise levels) defined in
/// StreamingAssets/instructions.txt as a single-condition template.
///
/// At runtime the template is expanded into 9 condition blocks in Latin Square
/// order determined by participantNumber % 9.
///
/// Each condition block starts with an auto-generated ConditionStart step that
/// switches the gaze visualization mode and launches the noise Python script.
///
/// Step types in the template file:
///   noise         → white noise (auto-ends)
///   task          → identification task (taskDurationSeconds)
///   assembly      → assembly task with video stream (assemblyDurationSeconds)
///   alignment     → position-alignment gate (video feed ON, Enter to advance)
///   questionnaire → freeform gate (Enter to advance)
///
/// Expert keyboard controls:
///   Enter  — start / advance after task or noise completes / end questionnaire
///   Delete — force-skip current task or noise
/// </summary>
public class ExperimentManager : MonoBehaviour, IOnEventCallback
{
    // ── Inspector ─────────────────────────────────────────────────────────
    [Header("Timings")]
    public float taskDurationSeconds      = 10f;
    public float assemblyDurationSeconds  = 180f;
    public float whiteNoiseDurationSeconds = 10f;
    [Range(0f, 1f)] public float whiteNoiseVolume = 0.4f;

    [Header("Resync")]
    [Tooltip("Expert re-broadcasts state every N seconds to correct Worker timer drift.")]
    public float resyncIntervalSeconds = 10f;

    [Header("Participant & Conditions")]
    [Tooltip("Participant number — determines Latin Square row (n % 9).")]
    public int    participantNumber      = 0;
    [Tooltip("Python executable name or full path (e.g. 'python' or 'C:/Python311/python.exe').")]
    public string pythonExecutable       = "python";
    [Tooltip("Directory that contains noise_low.py / noise_mid.py / noise_high.py.")]
    public string pythonScriptDirectory  = "";

    // ── Public Read-only State ────────────────────────────────────────────
    public ExperimentState CurrentState    { get; private set; } = ExperimentState.Idle;
    public StepType        CurrentStepType { get; private set; } = StepType.Noise;
    public float           RemainingSeconds{ get; private set; }
    public int             CurrentStepIndex{ get; private set; }
    public int             TotalSteps      { get; private set; }
    public bool            IsExpert        { get; private set; }

    // ── Events ────────────────────────────────────────────────────────────
    public event Action<ExperimentState>       OnStateChanged;
    public event Action<float>                 OnTimerUpdated;
    public event Action<string>                OnInstructionChanged;
    public event Action<int, int, StepType>    OnProgressChanged;

    // ── Condition Table (3 gaze × 3 noise = 9) ───────────────────────────
    private static readonly (VisualizationMode gaze, string noise, string script)[] CONDITIONS =
    {
        (VisualizationMode.Ray,     "noise_low",  "noise_low.py"),
        (VisualizationMode.Ray,     "noise_mid",  "noise_mid.py"),
        (VisualizationMode.Ray,     "noise_high", "noise_high.py"),
        (VisualizationMode.Circle,  "noise_low",  "noise_low.py"),
        (VisualizationMode.Circle,  "noise_mid",  "noise_mid.py"),
        (VisualizationMode.Circle,  "noise_high", "noise_high.py"),
        (VisualizationMode.Frustum, "noise_low",  "noise_low.py"),
        (VisualizationMode.Frustum, "noise_mid",  "noise_mid.py"),
        (VisualizationMode.Frustum, "noise_high", "noise_high.py"),
    };

    // ── Internal ──────────────────────────────────────────────────────────
    private List<ExperimentStep> steps            = new();
    private List<ExperimentStep> conditionTemplate = new();
    private int[]                conditionOrder;

    private AudioSource  audioSource;
    private AudioClip    noiseClip;
    private Coroutine    timerRoutine;
    private Coroutine    noiseRoutine;
    private Coroutine    resyncRoutine;
    private GazeHandler  expertGazeHandler;

    private const byte PHOTON_EVENT = 43;
    private const byte SYNC_REQUEST = 0xFF;

    // ── Init ──────────────────────────────────────────────────────────────

    public void Initialize(bool isExpert)
    {
        IsExpert = isExpert;
        PhotonNetwork.AddCallbackTarget(this);

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake  = false;
        audioSource.loop         = true;

        if (isExpert)
        {
            expertGazeHandler = GetComponent<GazeHandler>();
            StartCoroutine(LoadInstructions());
        }
        else
        {
            StartCoroutine(DelaySyncRequest());
        }
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
        if (noiseClip != null) Destroy(noiseClip);
    }

    // ── Expert Keyboard ───────────────────────────────────────────────────

    private void Update()
    {
        if (!IsExpert) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        bool enter = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
        bool del   = kb.deleteKey.wasPressedThisFrame;

        if (enter)
        {
            if      (CurrentState == ExperimentState.Ready)         StartExperiment();
            else if (CurrentState == ExperimentState.TaskComplete)   AdvanceStep();
            else if (CurrentState == ExperimentState.NoiseComplete)  AdvanceStep();
            else if (CurrentState == ExperimentState.Questionnaire)  AdvanceStep();
        }

        if (del && (CurrentState == ExperimentState.TaskRunning ||
                    CurrentState == ExperimentState.WhiteNoise))
            ForceSkip();
    }

    // ── Step Flow ─────────────────────────────────────────────────────────

    private void StartExperiment()
    {
        if (steps.Count == 0) { UnityEngine.Debug.LogWarning("[ExperimentManager] No steps loaded."); return; }
        CurrentStepIndex = 0;
        ExecuteCurrentStep();
    }

    private void AdvanceStep()
    {
        CurrentStepIndex++;
        if (CurrentStepIndex >= steps.Count)
        {
            Transition(ExperimentState.Finished);
            return;
        }
        ExecuteCurrentStep();
    }

    private void ForceSkip()
    {
        StopTimer();
        StopNoiseRoutine();
        audioSource.Stop();
        RemainingSeconds = 0f;
        OnTimerUpdated?.Invoke(0f);

        if (CurrentState == ExperimentState.TaskRunning)
        {
            if (IsExpert && CurrentStepType == StepType.Assembly) UnfollowWorker();
            Transition(ExperimentState.TaskComplete);
        }
        else
            Transition(ExperimentState.NoiseComplete);
    }

    private void ExecuteCurrentStep()
    {
        var step = steps[CurrentStepIndex];
        CurrentStepType = step.Type;

        switch (step.Type)
        {
            case StepType.Noise:
                RemainingSeconds = whiteNoiseDurationSeconds;
                Transition(ExperimentState.WhiteNoise);
                PlayWhiteNoise();
                StopNoiseRoutine();
                noiseRoutine = StartCoroutine(NoiseDuration());
                break;

            case StepType.Task:
                RemainingSeconds = taskDurationSeconds;
                Transition(ExperimentState.TaskRunning);
                RestartTimerAt(taskDurationSeconds);
                break;

            case StepType.Assembly:
                if (IsExpert) FollowWorker();
                RemainingSeconds = assemblyDurationSeconds;
                Transition(ExperimentState.TaskRunning);
                RestartTimerAt(assemblyDurationSeconds);
                break;

            case StepType.Alignment:
                if (IsExpert) AlignToWorker();
                Transition(ExperimentState.Questionnaire);
                break;

            case StepType.ConditionStart:
                if (IsExpert && step.ConditionIndex >= 0 && step.ConditionIndex < CONDITIONS.Length)
                {
                    var cond = CONDITIONS[step.ConditionIndex];
                    SetGazeMode(cond.gaze);
                    LaunchPythonScript(cond.script);
                }
                Transition(ExperimentState.Questionnaire);
                break;

            case StepType.Questionnaire:
                Transition(ExperimentState.Questionnaire);
                break;
        }
    }

    // ── Timer ─────────────────────────────────────────────────────────────

    public void RestartTimerAt(float startSeconds)
    {
        StopTimer();
        RemainingSeconds = Mathf.Max(0f, startSeconds);
        timerRoutine = StartCoroutine(RunTimer());
    }

    private void StopTimer()
    {
        if (timerRoutine != null) { StopCoroutine(timerRoutine); timerRoutine = null; }
    }

    private IEnumerator RunTimer()
    {
        while (RemainingSeconds > 0f)
        {
            RemainingSeconds -= Time.deltaTime;
            OnTimerUpdated?.Invoke(RemainingSeconds);
            yield return null;
        }
        RemainingSeconds = 0f;
        OnTimerUpdated?.Invoke(0f);
        if (IsExpert)
        {
            if (CurrentStepType == StepType.Assembly) UnfollowWorker();
            Transition(ExperimentState.TaskComplete);
        }
    }

    // ── Noise ─────────────────────────────────────────────────────────────

    private void StopNoiseRoutine()
    {
        if (noiseRoutine != null) { StopCoroutine(noiseRoutine); noiseRoutine = null; }
    }

    private void PlayWhiteNoise()
    {
        if (noiseClip == null) noiseClip = BuildNoiseClip();
        audioSource.clip   = noiseClip;
        audioSource.volume = whiteNoiseVolume;
        audioSource.Play();
    }

    private IEnumerator NoiseDuration()
    {
        float remaining = whiteNoiseDurationSeconds;
        while (remaining > 0f)
        {
            remaining -= Time.deltaTime;
            RemainingSeconds = remaining;
            OnTimerUpdated?.Invoke(remaining);
            yield return null;
        }
        RemainingSeconds = 0f;
        audioSource.Stop();
        OnTimerUpdated?.Invoke(0f);
        if (IsExpert) Transition(ExperimentState.NoiseComplete);
    }

    private AudioClip BuildNoiseClip()
    {
        int   rate    = AudioSettings.outputSampleRate;
        int   samples = rate * 5;
        float[] data  = new float[samples];
        var rng = new System.Random();
        for (int i = 0; i < samples; i++)
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        var clip = AudioClip.Create("WhiteNoise", samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ── State Transition ──────────────────────────────────────────────────

    private void Transition(ExperimentState next)
    {
        CurrentState = next;
        if (IsExpert) TotalSteps = steps.Count;

        string instruction = IsExpert ? GetCurrentInstruction() : string.Empty;
        OnStateChanged?.Invoke(next);
        OnInstructionChanged?.Invoke(instruction);
        OnProgressChanged?.Invoke(CurrentStepIndex, TotalSteps, CurrentStepType);

        if (IsExpert)
        {
            BroadcastState(next);
            StopResync();
            if (next == ExperimentState.TaskRunning || next == ExperimentState.WhiteNoise)
                resyncRoutine = StartCoroutine(PeriodicResync());
        }

        if (!IsExpert)
        {
            if (next == ExperimentState.WhiteNoise) PlayWhiteNoise();
            else                                    audioSource.Stop();

            // Mirror the Expert's streaming-mode toggle so the Worker's GazeVisualizer
            // uses the PCA camera FOV/aspect during Assembly instead of the PC camera FOV.
            bool streamingNeeded = next == ExperimentState.TaskRunning
                                   && CurrentStepType == StepType.Assembly;
            SetAllVisualizersStreamingMode(streamingNeeded);
        }
    }

    // ── Photon ────────────────────────────────────────────────────────────

    private void BroadcastState(ExperimentState state)
    {
        object[] data =
        {
            (byte)state,
            (byte)Mathf.Clamp(CurrentStepIndex, 0, 255),
            (byte)Mathf.Clamp(TotalSteps,       0, 255),
            (byte)CurrentStepType,
            RemainingSeconds
        };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(PHOTON_EVENT, data, opts, SendOptions.SendReliable);
    }

    public void BroadcastCurrentState() => BroadcastState(CurrentState);

    public void SendSyncRequest()
    {
        object[] req = { SYNC_REQUEST };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(PHOTON_EVENT, req, opts, SendOptions.SendReliable);
    }

    private IEnumerator DelaySyncRequest()
    {
        // Retry up to 3 times: if Expert hasn't responded and we're still Idle, try again.
        float[] delays = { 1.5f, 3f, 5f };
        foreach (float delay in delays)
        {
            yield return new WaitForSeconds(delay);
            if (CurrentState != ExperimentState.Idle) yield break;
            SendSyncRequest();
            UnityEngine.Debug.Log($"[ExperimentManager] Sync request sent (still Idle after {delay}s).");
        }
    }

    private void StopResync()
    {
        if (resyncRoutine != null) { StopCoroutine(resyncRoutine); resyncRoutine = null; }
    }

    private IEnumerator PeriodicResync()
    {
        while (true)
        {
            yield return new WaitForSeconds(resyncIntervalSeconds);
            if (IsExpert && (CurrentState == ExperimentState.TaskRunning ||
                             CurrentState == ExperimentState.WhiteNoise))
            {
                BroadcastState(CurrentState);
            }
            else break;
        }
        resyncRoutine = null;
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != PHOTON_EVENT) return;

        try
        {
            object[] data = (object[])photonEvent.CustomData;
            byte first = Convert.ToByte(data[0]);

            if (first == SYNC_REQUEST) { if (IsExpert) BroadcastState(CurrentState); return; }
            if (IsExpert) return;

            var   newState        = (ExperimentState)first;
            int   syncedStep      = Convert.ToInt32(data[1]);
            int   syncedTotal     = Convert.ToInt32(data[2]);
            var   syncedType      = (StepType)Convert.ToByte(data[3]);
            float syncedRemaining = data.Length > 4 ? Convert.ToSingle(data[4]) : taskDurationSeconds;

            UnityEngine.Debug.Log($"[ExperimentManager] OnEvent: state={newState}, step={syncedStep}/{syncedTotal}, remaining={syncedRemaining:F1}s");

            if (newState == CurrentState && syncedStep == CurrentStepIndex &&
                (newState == ExperimentState.TaskRunning || newState == ExperimentState.WhiteNoise))
            {
                if (newState == ExperimentState.TaskRunning)
                    RestartTimerAt(syncedRemaining);
                else
                {
                    StopNoiseRoutine();
                    noiseRoutine = StartCoroutine(WorkerNoiseMirror(syncedRemaining));
                }
                return;
            }

            CurrentStepIndex = syncedStep;
            TotalSteps       = syncedTotal;
            CurrentStepType  = syncedType;

            StopTimer();
            StopNoiseRoutine();
            RemainingSeconds = syncedRemaining;

            Transition(newState);

            if (newState == ExperimentState.TaskRunning)
                RestartTimerAt(syncedRemaining);
            else if (newState == ExperimentState.WhiteNoise)
            {
                StopNoiseRoutine();
                noiseRoutine = StartCoroutine(WorkerNoiseMirror(syncedRemaining));
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[ExperimentManager] OnEvent exception: {ex}");
        }
    }

    private IEnumerator WorkerNoiseMirror(float remaining)
    {
        while (remaining > 0f)
        {
            remaining -= Time.deltaTime;
            RemainingSeconds = remaining;
            OnTimerUpdated?.Invoke(remaining);
            yield return null;
        }
        RemainingSeconds = 0f;
        OnTimerUpdated?.Invoke(0f);
        noiseRoutine = null;
    }

    // ── Instructions Loading & Expansion ─────────────────────────────────

    private IEnumerator LoadInstructions()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "instructions.txt");

#if UNITY_ANDROID && !UNITY_EDITOR
        using var req = UnityWebRequest.Get(path);
        yield return req.SendWebRequest();
        if (req.result == UnityWebRequest.Result.Success)
            ParseTemplate(req.downloadHandler.text);
        else
            UnityEngine.Debug.LogError($"[ExperimentManager] Load failed: {req.error}");
#else
        if (File.Exists(path))
            ParseTemplate(File.ReadAllText(path));
        else
            UnityEngine.Debug.LogError($"[ExperimentManager] instructions.txt not found at: {path}");
        yield return null;
#endif

        BuildConditionOrder();
        ExpandTemplate();
        TotalSteps = steps.Count;
        Transition(ExperimentState.Ready);
    }

    private void ParseTemplate(string text)
    {
        conditionTemplate.Clear();
        var block = new List<string>();

        void Commit()
        {
            if (block.Count == 0) return;
            var step = new ExperimentStep();
            string typeLine = block[0].Trim().ToLowerInvariant();
            step.Type = typeLine switch
            {
                "noise"         => StepType.Noise,
                "questionnaire" => StepType.Questionnaire,
                "assembly"      => StepType.Assembly,
                "alignment"     => StepType.Alignment,
                _               => StepType.Task
            };
            step.Instruction = block.Count > 1
                ? string.Join("\n", block.GetRange(1, block.Count - 1))
                : string.Empty;
            conditionTemplate.Add(step);
            block.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;
            if (line == "===") Commit();
            else block.Add(line);
        }
        Commit();

        UnityEngine.Debug.Log($"[ExperimentManager] Template: {conditionTemplate.Count} steps/condition.");
    }

    // Cyclic Latin Square: participant n → row (n % 9)
    private void BuildConditionOrder()
    {
        int n     = CONDITIONS.Length;
        int start = participantNumber % n;
        conditionOrder = new int[n];
        for (int i = 0; i < n; i++)
            conditionOrder[i] = (start + i) % n;

        UnityEngine.Debug.Log($"[ExperimentManager] Participant {participantNumber} → condition order: [{string.Join(", ", conditionOrder)}]");
    }

    // Expand template into 9 condition blocks, each prefixed with an auto-generated ConditionStart step
    private void ExpandTemplate()
    {
        steps.Clear();
        int n = CONDITIONS.Length;

        for (int c = 0; c < n; c++)
        {
            int condIdx = conditionOrder[c];
            var cond    = CONDITIONS[condIdx];

            steps.Add(new ExperimentStep
            {
                Type           = StepType.ConditionStart,
                ConditionIndex = condIdx,
                Instruction    = $"[条件 {c + 1}/{n}]  ガゼ: {cond.gaze}  |  ノイズ: {cond.noise}\n" +
                                 $"スクリプト {cond.script} の起動を確認したら Enter を押してください。"
            });

            foreach (var t in conditionTemplate)
                steps.Add(new ExperimentStep
                {
                    Type           = t.Type,
                    Instruction    = t.Instruction,
                    ConditionIndex = -1
                });
        }

        UnityEngine.Debug.Log($"[ExperimentManager] Expanded: {n} conditions × {conditionTemplate.Count + 1} steps = {steps.Count} total.");
    }

    // ── Condition Actions (Expert only) ───────────────────────────────────

    /// <summary>Find the Worker's PostureHandler (the one not owned by this client).</summary>
    private PostureHandler FindWorkerPosture()
    {
        foreach (var ph in UnityEngine.Object.FindObjectsByType<PostureHandler>(FindObjectsSortMode.None))
        {
            if (!ph.photonView.IsMine) return ph;
        }
        UnityEngine.Debug.LogWarning("[ExperimentManager] Worker PostureHandler not found.");
        return null;
    }

    private void AlignToWorker()
    {
        var ph = FindWorkerPosture();
        if (ph == null) return;

        var ch = UnityEngine.Object.FindAnyObjectByType<ConnectionHandler>();
        if (ch != null)
        {
            ch.TeleportTo(ph.transform.position, ph.transform.rotation);
            UnityEngine.Debug.Log($"[ExperimentManager] Expert aligned to Worker: pos={ph.transform.position}");
        }
        else
            UnityEngine.Debug.LogWarning("[ExperimentManager] AlignToWorker: ConnectionHandler not found.");
    }

    /// <summary>Lock Expert camera to Worker's head (Assembly中).</summary>
    private void FollowWorker()
    {
        var ph = FindWorkerPosture();
        if (ph == null) return;

        var ch = UnityEngine.Object.FindAnyObjectByType<ConnectionHandler>();
        if (ch != null)
        {
            ch.SetFollowTarget(ph.transform);
            UnityEngine.Debug.Log("[ExperimentManager] Expert follow mode started.");
        }

        // GazeVisualizer の FOV を PCA カメラに合わせる
        SetAllVisualizersStreamingMode(true);
    }

    /// <summary>Release Expert camera from Worker追従.</summary>
    private void UnfollowWorker()
    {
        var ch = UnityEngine.Object.FindAnyObjectByType<ConnectionHandler>();
        if (ch != null)
        {
            ch.ClearFollowTarget();
            UnityEngine.Debug.Log("[ExperimentManager] Expert follow mode ended.");
        }

        // GazeVisualizer の FOV を Expert カメラに戻す
        SetAllVisualizersStreamingMode(false);
    }

    private void SetAllVisualizersStreamingMode(bool streaming)
    {
        foreach (var viz in UnityEngine.Object.FindObjectsByType<GazeVisualizer>(FindObjectsSortMode.None))
        {
            viz.SetStreamingMode(streaming);
        }
    }

    private void SetGazeMode(VisualizationMode mode)
    {
        if (expertGazeHandler == null) expertGazeHandler = GetComponent<GazeHandler>();
        if (expertGazeHandler != null)
        {
            expertGazeHandler.CurrentMode = mode;
            UnityEngine.Debug.Log($"[ExperimentManager] Gaze mode → {mode}");
        }
    }

    private void LaunchPythonScript(string scriptName)
    {
#if !UNITY_ANDROID
        string scriptPath = string.IsNullOrEmpty(pythonScriptDirectory)
            ? scriptName
            : Path.Combine(pythonScriptDirectory, scriptName);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = pythonExecutable,
                Arguments       = $"\"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow  = false
            });
            UnityEngine.Debug.Log($"[ExperimentManager] Launched: {pythonExecutable} \"{scriptPath}\"");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[ExperimentManager] Python launch failed: {ex.Message}");
        }
#endif
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string GetCurrentInstruction()
    {
        if (steps == null || CurrentStepIndex >= steps.Count) return string.Empty;
        return steps[CurrentStepIndex].Instruction;
    }

    public string GetInstruction(int idx)
    {
        if (steps == null || idx >= steps.Count) return string.Empty;
        return steps[idx].Instruction;
    }
}
