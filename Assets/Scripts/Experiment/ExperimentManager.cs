using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
#if UNITY_STANDALONE || UNITY_EDITOR
using System.Diagnostics;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

public enum StepType : byte
{
    Noise          = 0,
    Task           = 1,
    Questionnaire  = 2,
    Assembly       = 3,
    Alignment      = 4,  // position-alignment gate — video feed ON, no timer, Enter to advance
    ConditionStart = 5,  // auto-generated: switches gaze mode, then questionnaire gate
    Launch         = 6,  // launches Python script, auto-advances immediately
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
    public string   Instruction      = string.Empty; // Remote Expert
    public string   LocalInstruction = string.Empty; // Local Worker
    public int      ConditionIndex   = -1;           // set for ConditionStart / Launch steps
    public string   ScriptArgs       = string.Empty; // baked in at expand time for Launch steps
}

/// <summary>
/// Drives a 9-condition experiment (3 gaze modes × 3 noise levels) structured
/// as 3 BLOCKED tracking-method blocks, each containing 3 gaze-mode sub-conditions.
///
///   Block 0 — Tobii infrared (noise_low, 32-bit Python)
///   Block 1 — Webcam (noise_mid, 64-bit Python)
///   Block 2 — High noise (noise_high, 64-bit Python)
///
/// Block order and gaze-mode order within each block are counterbalanced by
/// participantNumber: block order uses (n % 6), gaze order uses ((n/6) % 6).
/// This gives 36 distinct orderings, covering up to 36 participants.
///
/// Python script is launched ONCE at the start of each block (3 launches total).
/// The per-condition template in instructions.txt is repeated 3 times per block;
/// any Launch steps in the template are ignored (launches are code-generated).
///
/// Step types recognised in instructions.txt:
///   noise         → white noise (auto-ends)
///   task          → identification task (taskDurationSeconds)
///   assembly      → assembly task with video stream (assemblyDurationSeconds)
///   alignment     → position-alignment gate (video feed ON, Enter to advance)
///   questionnaire → freeform gate (Enter to advance)
///
/// Expert keyboard:
///   Enter  — start / advance after task or noise completes / end questionnaire
///   Delete — force-skip current task or noise
/// </summary>
public class ExperimentManager : MonoBehaviour, IOnEventCallback
{
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
    [Tooltip("32-bit Python executable — used for Tobii infrared (noise_low). E.g. C:/Python311_32/python.exe")]
    public string pythonExecutable32     = "python";
    [Tooltip("64-bit Python executable — used for webcam / high-noise scripts. E.g. C:/Python311/python.exe")]
    public string pythonExecutable64     = "python";
    [Tooltip("Root directory of the EyeTrackToOSCData repository.")]
    public string pythonScriptDirectory  = "";
    [Tooltip("Turn it on when you don't have Tobii / when you want to skip Tobii launch. It will skip the launch of the 32-bit script.")]
    public bool   skipTobiiLaunch        = false;

    [Header("Python Script Args (per block)")]
    [Tooltip("CLI args for Block 0 — Tobii infrared script. Usually empty.")]
    public string tobiiScriptArgs     = "";
    [Tooltip("CLI args for Block 1 — Webcam execution script.")]
    public string webcamScriptArgs    = "--weights models/L2CSNet_gaze360.pkl --osc-port 8000";
    [Tooltip("CLI args for Block 2 — High-noise script. Usually empty.")]
    public string highNoiseScriptArgs = "";

    [Header("Python Calibration Args (Webcam only)")]
    [Tooltip("Webcam calibration args — same script as execution, run before the block. Tobii calibration is done manually.")]
    public string webcamCalibArgs     = "--calibrate --weights models/L2CSNet_gaze360.pkl --osc-port 0";

    public ExperimentState CurrentState    { get; private set; } = ExperimentState.Idle;
    public StepType        CurrentStepType { get; private set; } = StepType.Noise;
    public float           RemainingSeconds{ get; private set; }
    public int             CurrentStepIndex{ get; private set; }
    public int             TotalSteps      { get; private set; }
    public bool            IsExpert        { get; private set; }
    public int             CurrentConditionIndex => currentConditionIndex;

    public (VisualizationMode gaze, string noise) GetConditionInfo(int idx)
    {
        if (idx < 0 || idx >= CONDITIONS.Length) return (VisualizationMode.Ray, "unknown");
        return (CONDITIONS[idx].gaze, CONDITIONS[idx].noise);
    }

    public event Action<ExperimentState>       OnStateChanged;
    public event Action<float>                 OnTimerUpdated;
    public event Action<string>                OnInstructionChanged;
    public event Action<int, int, StepType>    OnProgressChanged;

    // Conditions 0-2: Tobii infrared (noise_low, 32-bit Python)
    // Conditions 3-5: Webcam        (noise_mid, 64-bit Python)
    // Conditions 6-8: High noise    (noise_high, 64-bit Python)
    //
    // script: path relative to pythonScriptDirectory (repo root)
    // CLI args are set per-block in the Inspector (tobiiScriptArgs / webcamScriptArgs / highNoiseScriptArgs)
    private static readonly (VisualizationMode gaze, string noise, string script, bool use32bit)[] CONDITIONS =
    {
        // Block 0 — Tobii infrared
        (VisualizationMode.Ray,     "noise_low",  @"scr_infrared\EyeTracking_Py32Only.py", true),
        (VisualizationMode.Circle,  "noise_low",  @"scr_infrared\EyeTracking_Py32Only.py", true),
        (VisualizationMode.Frustum, "noise_low",  @"scr_infrared\EyeTracking_Py32Only.py", true),
        // Block 1 — Webcam
        (VisualizationMode.Ray,     "noise_mid",  @"scr_webcam\webcam_gaze_tracker.py",    false),
        (VisualizationMode.Circle,  "noise_mid",  @"scr_webcam\webcam_gaze_tracker.py",    false),
        (VisualizationMode.Frustum, "noise_mid",  @"scr_webcam\webcam_gaze_tracker.py",    false),
        // Block 2 — High noise
        (VisualizationMode.Ray,     "noise_high", @"scr_webcam\webcam_gaze_tracker.py",    false),
        (VisualizationMode.Circle,  "noise_high", @"scr_webcam\webcam_gaze_tracker.py",    false),
        (VisualizationMode.Frustum, "noise_high", @"scr_webcam\webcam_gaze_tracker.py",    false),
    };

    private const int BLOCK_SIZE = 3; // gaze modes per tracking block

    // All 6 permutations of [0,1,2] for counterbalancing
    private static readonly int[][] PERMUTATIONS_3 =
    {
        new[]{0,1,2}, new[]{0,2,1}, new[]{1,0,2},
        new[]{1,2,0}, new[]{2,0,1}, new[]{2,1,0}
    };

    private List<ExperimentStep> steps            = new();
    private List<ExperimentStep> conditionTemplate = new();
    private int[]                conditionOrder;

    private AudioSource  audioSource;
    private AudioClip    noiseClip;
    private Coroutine    timerRoutine;
    private Coroutine    noiseRoutine;
    private Coroutine    resyncRoutine;
    private GazeHandler  expertGazeHandler;
    private int          currentConditionIndex = -1; // updated at each ConditionStart
#if UNITY_STANDALONE || UNITY_EDITOR
    private Process      noisePythonProcess;
#endif

    private const byte PHOTON_EVENT = 43;
    private const byte SYNC_REQUEST = 0xFF;

    public void Initialize(bool isExpert)
    {
        IsExpert = isExpert;
        PhotonNetwork.AddCallbackTarget(this);

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake  = false;
        audioSource.loop         = true;

        // expertGazeHandler is injected via SetGazeHandler() before Initialize().
        // GazeHandler lives on the player prefab, not this GameObject.

        StartCoroutine(LoadInstructions());

        if (!isExpert)
            StartCoroutine(DelaySyncRequest());
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
        if (noiseClip != null) Destroy(noiseClip);
        KillNoisePythonProcess();
    }

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
                    currentConditionIndex = step.ConditionIndex;
                    SetGazeMode(CONDITIONS[step.ConditionIndex].gaze);
                }
                Transition(ExperimentState.Questionnaire);
                break;

            case StepType.Launch:
                if (IsExpert && step.ConditionIndex >= 0 && step.ConditionIndex < CONDITIONS.Length)
                {
                    var cond = CONDITIONS[step.ConditionIndex];
                    LaunchPythonScript(cond.script, cond.use32bit, step.ScriptArgs);
                }
                StartCoroutine(AdvanceNextFrame());
                break;

            case StepType.Questionnaire:
                Transition(ExperimentState.Questionnaire);
                break;
        }
    }

    private IEnumerator AdvanceNextFrame()
    {
        yield return null;
        AdvanceStep();
    }

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

    private void Transition(ExperimentState next)
    {
        CurrentState = next;
        if (IsExpert) TotalSteps = steps.Count;

        // Do not fire step instruction text for states that have their own fixed
        // messages in the UI. TaskComplete/NoiseComplete in particular must not
        // be overridden — doing so would show the just-finished step's instruction
        // one beat late instead of the "task ended, press Enter" message.
        bool suppressInstruction = next == ExperimentState.Ready
                                || next == ExperimentState.Idle
                                || next == ExperimentState.Finished
                                || next == ExperimentState.TaskComplete
                                || next == ExperimentState.NoiseComplete;
        string instruction = suppressInstruction ? string.Empty : GetCurrentInstruction();
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
        if (IsExpert) Transition(ExperimentState.Ready);
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
                "launch"        => StepType.Launch,
                _               => StepType.Task
            };

            // For Launch steps, parse [args] key.
            if (step.Type == StepType.Launch)
            {
                for (int i = 1; i < block.Count; i++)
                {
                    string l = block[i].Trim();
                    if (l.StartsWith("[args]"))
                        step.ScriptArgs = l.Substring("[args]".Length).Trim();
                }
                conditionTemplate.Add(step);
                block.Clear();
                return;
            }

            // If the block contains [remote]/[local] markers, split accordingly.
            // Without markers, all lines go to Instruction (Remote) for backward compat.
            bool hasMarkers = block.Exists(l => l.Trim() == "[remote]" || l.Trim() == "[local]");
            if (hasMarkers)
            {
                var remoteLines = new List<string>();
                var localLines  = new List<string>();
                List<string> current = null; // lines before any marker are ignored

                for (int i = 1; i < block.Count; i++)
                {
                    string l = block[i].Trim();
                    if      (l == "[remote]") { current = remoteLines; }
                    else if (l == "[local]")  { current = localLines; }
                    else                      { current?.Add(block[i]); }
                }

                step.Instruction      = string.Join("\n", remoteLines).Trim();
                step.LocalInstruction = string.Join("\n", localLines).Trim();
            }
            else
            {
                step.Instruction = block.Count > 1
                    ? string.Join("\n", block.GetRange(1, block.Count - 1)).Trim()
                    : string.Empty;
            }

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

    // Blocked counterbalancing:
    //   block order  → participantNumber % 6        (6 permutations of 3 blocks)
    //   gaze order   → (participantNumber / 6) % 6  (6 permutations within each block)
    // 6 × 6 = 36 distinct orderings — covers up to 36 participants.
    private void BuildConditionOrder()
    {
        int blockPerm = participantNumber % 6;
        int gazePerm  = (participantNumber / 6) % 6;

        int[] blockOrder = PERMUTATIONS_3[blockPerm];
        int[] gazeOrder  = PERMUTATIONS_3[gazePerm];

        conditionOrder = new int[CONDITIONS.Length];
        for (int b = 0; b < BLOCK_SIZE; b++)
            for (int g = 0; g < BLOCK_SIZE; g++)
                conditionOrder[b * BLOCK_SIZE + g] = blockOrder[b] * BLOCK_SIZE + gazeOrder[g];

        UnityEngine.Debug.Log($"[ExperimentManager] P{participantNumber} → blocks [{string.Join(",", blockOrder)}], gazes [{string.Join(",", gazeOrder)}]");
        UnityEngine.Debug.Log($"[ExperimentManager] Condition order: [{string.Join(", ", conditionOrder)}]");
    }

    // Expand template into 3 blocks × 3 conditions.
    // A code-generated Launch step fires ONCE at the start of each block (before ConditionStart).
    // Any Launch steps inside the template are skipped — args now live in the CONDITIONS table.
    private void ExpandTemplate()
    {
        steps.Clear();
        int numBlocks = CONDITIONS.Length / BLOCK_SIZE;

        for (int c = 0; c < CONDITIONS.Length; c++)
        {
            int condIdx      = conditionOrder[c];
            var cond         = CONDITIONS[condIdx];
            bool isBlockStart = (c % BLOCK_SIZE == 0);
            int  blockNum    = c / BLOCK_SIZE + 1;

            if (isBlockStart)
            {
                int    blockIdx  = c / BLOCK_SIZE;
                string calibArgs = GetBlockCalibArgs(blockIdx);
                string execArgs  = GetBlockArgs(blockIdx);
                string argsNote  = string.IsNullOrEmpty(execArgs) ? "" : $"\n{execArgs}";

                if (!string.IsNullOrEmpty(calibArgs))
                {
                    // 1. Same script, calibration args
                    steps.Add(new ExperimentStep
                    {
                        Type             = StepType.Launch,
                        ConditionIndex   = condIdx,
                        ScriptArgs       = calibArgs,
                        Instruction      = $"[Block {blockNum}/{numBlocks}]  キャリブレーション開始\n{cond.script}\n{calibArgs}",
                        LocalInstruction = $"[Block {blockNum}/{numBlocks}]  キャリブレーション中です。しばらくお待ちください。",
                    });
                    // 2. Expert confirms calibration done
                    steps.Add(new ExperimentStep
                    {
                        Type             = StepType.Questionnaire,
                        Instruction      = $"[Block {blockNum}/{numBlocks}]  キャリブレーションを実行してください。\n完了したら [Enter] を押してください。",
                        LocalInstruction = $"[Block {blockNum}/{numBlocks}]  キャリブレーション中です。担当者の指示をお待ちください。",
                    });
                }

                // 3. Same script, execution args — kills the calibration process first
                steps.Add(new ExperimentStep
                {
                    Type             = StepType.Launch,
                    ConditionIndex   = condIdx,
                    ScriptArgs       = execArgs,
                    Instruction      = $"[Block {blockNum}/{numBlocks}]  {cond.noise}  ({(cond.use32bit ? "32-bit" : "64-bit")})\n{cond.script}{argsNote}",
                    LocalInstruction = $"[Block {blockNum}/{numBlocks}]  次のブロックを準備中です。しばらくお待ちください。",
                });
            }

            // ConditionStart: switch gaze mode, open ready gate
            string readyNote = isBlockStart
                ? "実行スクリプト起動済み。アイトラッキングを確認し、[Enter] で開始してください。"
                : "次の視線モードに切り替えました。[Enter] で続けてください。";
            steps.Add(new ExperimentStep
            {
                Type             = StepType.ConditionStart,
                ConditionIndex   = condIdx,
                Instruction      = $"[Block {blockNum}/{numBlocks}  Cond {c + 1}/9]  Gaze: {cond.gaze}  |  Noise: {cond.noise}\n{readyNote}",
                LocalInstruction = $"[Block {blockNum}/{numBlocks}  Cond {c + 1}/9]  次の条件を準備中です。しばらくお待ちください。",
            });

            // Per-condition steps from template (skip any Launch — handled above)
            foreach (var t in conditionTemplate)
            {
                if (t.Type == StepType.Launch) continue;
                steps.Add(new ExperimentStep
                {
                    Type             = t.Type,
                    Instruction      = t.Instruction,
                    LocalInstruction = t.LocalInstruction,
                    ScriptArgs       = t.ScriptArgs,
                    ConditionIndex   = -1
                });
            }
        }

        UnityEngine.Debug.Log($"[ExperimentManager] Expanded: {numBlocks} blocks × {BLOCK_SIZE} conds × {conditionTemplate.Count} template steps = {steps.Count} total.");
    }

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

    /// <summary>Lock Expert camera to Worker's head during Assembly.</summary>
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

        // Switch GazeVisualizer FOV to match the PCA (streaming) camera during Assembly
        SetAllVisualizersStreamingMode(true);
    }

    /// <summary>Release Expert camera from Worker follow.</summary>
    private void UnfollowWorker()
    {
        var ch = UnityEngine.Object.FindAnyObjectByType<ConnectionHandler>();
        if (ch != null)
        {
            ch.ClearFollowTarget();
            UnityEngine.Debug.Log("[ExperimentManager] Expert follow mode ended.");
        }

        // Restore GazeVisualizer FOV to the Expert's own camera
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

    private string GetBlockArgs(int blockIndex) => blockIndex switch
    {
        0 => tobiiScriptArgs,
        1 => webcamScriptArgs,
        2 => highNoiseScriptArgs,
        _ => ""
    };

    // Only Block 1 (webcam) has a calibration step; Tobii is calibrated manually.
    private string GetBlockCalibArgs(int blockIndex) => blockIndex == 1 ? webcamCalibArgs : "";

    private void KillNoisePythonProcess()
    {
#if UNITY_STANDALONE || UNITY_EDITOR
        if (noisePythonProcess == null) return;
        try
        {
            if (!noisePythonProcess.HasExited)
            {
                noisePythonProcess.Kill();
                UnityEngine.Debug.Log("[ExperimentManager] Previous Python process killed.");
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[ExperimentManager] Kill failed: {ex.Message}");
        }
        finally
        {
            noisePythonProcess.Dispose();
            noisePythonProcess = null;
        }
#endif
    }

    private void LaunchPythonScript(string scriptName, bool use32bit, string args = "")
    {
#if UNITY_STANDALONE || UNITY_EDITOR
        if (use32bit && skipTobiiLaunch)
        {
            UnityEngine.Debug.Log("[ExperimentManager] Tobii launch skipped (skipTobiiLaunch=true).");
            return;
        }

        KillNoisePythonProcess();

        string exe        = use32bit ? pythonExecutable32 : pythonExecutable64;
        string scriptPath = string.IsNullOrEmpty(pythonScriptDirectory)
            ? scriptName
            : Path.Combine(pythonScriptDirectory, scriptName);

        // Normalize to backslashes — cmd.exe rejects mixed-slash paths.
        scriptPath = scriptPath.Replace('/', '\\');
        exe        = exe.Replace('/', '\\');

        // Build argument string — append extra args after script path if provided.
        string extraArgs = string.IsNullOrEmpty(args) ? "" : $" {args}";
        UnityEngine.Debug.Log($"[ExperimentManager] Launching: {exe} \"{scriptPath}\"{extraArgs}");
        try
        {
            // cmd /k keeps the window open after the script exits or crashes,
            // so any Python error traceback remains visible.
            // Outer quotes required by cmd when the inner command contains spaces.
            // WorkingDirectory = script's own folder so relative paths (models/, etc.) resolve correctly.
            string workDir = Path.GetDirectoryName(scriptPath);

            noisePythonProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = "cmd.exe",
                Arguments        = $"/k \"\"{exe}\" \"{scriptPath}\"{extraArgs}\"",
                WorkingDirectory = workDir,
                UseShellExecute  = true,
                CreateNoWindow   = false
            });
            UnityEngine.Debug.Log($"[ExperimentManager] Launch OK. PID={noisePythonProcess?.Id}");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[ExperimentManager] Launch FAILED: {ex.Message}");
        }
#endif
    }

    private string GetCurrentInstruction()
    {
        if (steps == null || CurrentStepIndex >= steps.Count) return string.Empty;
        var step = steps[CurrentStepIndex];
        return IsExpert ? step.Instruction : step.LocalInstruction;
    }

    public string GetInstruction(int idx)
    {
        if (steps == null || idx >= steps.Count) return string.Empty;
        var step = steps[idx];
        return IsExpert ? step.Instruction : step.LocalInstruction;
    }
}
