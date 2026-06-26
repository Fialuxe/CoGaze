using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

/// <summary>
/// Drives a 10-condition experiment (3 gaze modes × 3 noise levels + 1 no-gaze control).
/// Condition definitions and counterbalancing tables live in ExperimentDesign.
/// UI messages are loaded from StreamingAssets/ui_messages.txt via MessageBank.
///
/// participantOrderIndex 0-23:  ÷6 = group order (Williams 4×4),  %6 = gaze-mode order (all 6 perms)
///
/// Expert keyboard:
///   Enter  — start / advance after task or noise completes / end questionnaire
///   Delete — force-skip current task or noise
///   R      — retry calibration after FAIL (during ConditionStart)
/// </summary>
public class ExperimentManager2 : MonoBehaviour, IOnEventCallback
{
    // -----------------------------------------------------------------------
    // Inspector fields
    // -----------------------------------------------------------------------

    [Header("Timings")]
    public float taskDurationSeconds       = 10f;
    public float assemblyDurationSeconds   = 180f;
    public float whiteNoiseDurationSeconds = 60f;
    [Range(0f, 1f)] public float whiteNoiseVolume    = 0.30f;
    [Range(0f, 1f)] public float natureSoundVolume   = 0.25f;

    [Header("Resync")]
    [Tooltip("Expert re-broadcasts state every N seconds to correct Worker timer drift.")]
    public float resyncIntervalSeconds = 10f;

    [Header("Participant & Conditions")]
    [Tooltip("Participant number 窶・used for logging only.")]
    public int participantNumber = 0;
    [Range(0, 23), SerializeField] public int participantOrderIndex = 0;  // 0-23 (÷6 = group order, %6 = gaze-mode order)
    [SerializeField] public string participantId = "P00";

    // -----------------------------------------------------------------------
    // Public properties
    // -----------------------------------------------------------------------

    public ExperimentState CurrentState    { get; private set; } = ExperimentState.Idle;
    public StepType        CurrentStepType { get; private set; } = StepType.Noise;
    public float           RemainingSeconds{ get; private set; }
    public int             CurrentStepIndex{ get; private set; }
    public int             TotalSteps      { get; private set; }
    public bool            IsExpert        { get; private set; }

    public int             CurrentConditionIndex       { get; private set; } = -1;
    public int             CurrentConditionRunPosition { get; private set; } = -1;
    public ConditionType   CurrentConditionType        { get; private set; }
    public GazeMode        CurrentGazeMode             { get; private set; }
    public string          CurrentConditionName        { get; private set; } = string.Empty;

    public (string gaze, string noise) GetConditionInfo(int idx) => ExperimentDesign.GetConditionInfo(idx);

    public event Action<ExperimentState>       OnStateChanged;
    public event Action<float>                 OnTimerUpdated;
    public event Action<string>                OnInstructionChanged;
    public event Action<int, int, StepType>    OnProgressChanged;

    // -----------------------------------------------------------------------
    // Private fields
    // -----------------------------------------------------------------------

    private OscSessionManager _oscSession;
    private bool              _oscSessionActive     = false;
    private bool              _templateLoaded       = false;
    private bool              _firstPongReceived    = false;
    private System.Action     _pongForReadyHandler;
    private Coroutine         _oscReadyTimeoutCo;
    private bool              _calibrationPending   = false;
    private bool              _calibrationFailed    = false;
    private System.Action<string, string> _pendingCalibAckHandler;

    private List<ExperimentStep> steps            = new();
    private List<ExperimentStep> conditionTemplate = new();
    private int[]                conditionOrder;   // WilliamsTable[participantOrderIndex]

    private AudioSource  audioSource;
    private AudioSource  _natureSoundSource;
    private AudioClip    noiseClip;
    private AudioClip    _natureSoundClip;
    private Coroutine    timerRoutine;
    private Coroutine    noiseRoutine;
    private Coroutine    resyncRoutine;
    private GazeHandler  expertGazeHandler;

    private QuestionnaireManager questionnaireManager;
    private IdentificationTask   identificationTask;

    private const byte PHOTON_EVENT = 43;
    private const byte SYNC_REQUEST = 0xFF;

    // -----------------------------------------------------------------------
    // Initialization
    // -----------------------------------------------------------------------

    public void Initialize(bool isExpert)
    {
        IsExpert = isExpert;
        CurrentState = ExperimentState.Setup;
        PhotonNetwork.AddCallbackTarget(this);

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake  = false;
        audioSource.loop         = true;

        _natureSoundSource = gameObject.AddComponent<AudioSource>();
        _natureSoundSource.spatialBlend = 0f;
        _natureSoundSource.playOnAwake  = false;
        _natureSoundSource.loop         = true;
        _natureSoundClip = Resources.Load<AudioClip>("Audio/rain_loop");
        if (_natureSoundClip == null)
            FileLogger.Log("Experiment", "[ExperimentManager2] Nature sound not found at Resources/Audio/rain_loop — brown noise only. Add a looping rain WAV to Assets/Resources/Audio/rain_loop.wav to enable.");
        else
            FileLogger.Log("Experiment", $"[ExperimentManager2] Nature sound loaded: {_natureSoundClip.name}");

        // expertGazeHandler is injected via SetGazeHandler() before Initialize() is called.
        // Do NOT call GetComponent<GazeHandler>() here — GazeHandler lives on the player
        // prefab, not on this GameObject, so it would return null and overwrite the injected ref.

        StartCoroutine(LoadInstructions());

        if (!isExpert)
            StartCoroutine(DelaySyncRequest());

        questionnaireManager = FindAnyObjectByType<QuestionnaireManager>();
        if (questionnaireManager != null)
        {
            questionnaireManager.participantId     = participantId;
            questionnaireManager.participantNumber = participantOrderIndex;
            if (isExpert) questionnaireManager.OnQuestionnaireComplete += AdvanceStepFromQuestionnaire;
        }
        identificationTask = FindAnyObjectByType<IdentificationTask>();
        if (isExpert && identificationTask != null)
            identificationTask.OnTaskComplete += AdvanceStepFromTask;

        if (isExpert)
        {
            _oscSession = FindAnyObjectByType<OscSessionManager>();
            if (_oscSession != null)
            {
                FileLogger.Log("Experiment", "[ExperimentManager] OscSessionManager found — Python OSC enabled.");
                _oscSession.OnCalibrationResult += HandleCalibrationResult;

                // Subscribe before LoadInstructions so we never miss a pong that arrives early
                _pongForReadyHandler = () => {
                    if (_firstPongReceived) return;
                    _firstPongReceived = true;
                    _oscSession.OnPong -= _pongForReadyHandler;
                    _pongForReadyHandler = null;
                    if (_oscReadyTimeoutCo != null) { StopCoroutine(_oscReadyTimeoutCo); _oscReadyTimeoutCo = null; }
                    TryTransitionToReady();
                };
                _oscSession.OnPong += _pongForReadyHandler;
                _oscSession.Ping();
                _oscReadyTimeoutCo = StartCoroutine(OscReadyTimeout());
            }
            else
                Debug.LogError("[ExperimentManager] OscSessionManager not in scene — Python OSC disabled.");
        }
    }

    private void OnDestroy()
    {
        if (IsExpert)
        {
            if (_oscReadyTimeoutCo != null) { StopCoroutine(_oscReadyTimeoutCo); _oscReadyTimeoutCo = null; }
            if (questionnaireManager != null) questionnaireManager.OnQuestionnaireComplete -= AdvanceStepFromQuestionnaire;
            if (identificationTask != null)   identificationTask.OnTaskComplete -= AdvanceStepFromTask;
            if (_pongForReadyHandler != null && _oscSession != null)
            {
                _oscSession.OnPong -= _pongForReadyHandler;
                _pongForReadyHandler = null;
            }
            if (_oscSession != null)
            {
                _oscSession.OnCalibrationResult -= HandleCalibrationResult;
                if (_pendingCalibAckHandler != null)
                {
                    _oscSession.OnAck -= _pendingCalibAckHandler;
                    _pendingCalibAckHandler = null;
                }
            }
        }
        PhotonNetwork.RemoveCallbackTarget(this);
        if (noiseClip != null) Destroy(noiseClip);
    }

    // -----------------------------------------------------------------------
    // Expert keyboard input
    // -----------------------------------------------------------------------

    private float _lastEnterTime = -1f;

    private void Update()
    {
        if (!IsExpert) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        bool enter = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
        bool del   = kb.deleteKey.wasPressedThisFrame;

        // 500ms debounce prevents accidental double-advance from a single key bounce.
        if (enter && Time.time - _lastEnterTime > 0.5f)
        {
            if (CurrentState == ExperimentState.Ready)
            {
                _lastEnterTime = Time.time;
                StartExperiment();
            }
            else if (CurrentState == ExperimentState.TaskComplete  ||
                     CurrentState == ExperimentState.NoiseComplete ||
                     CurrentState == ExperimentState.Questionnaire)
            {
                if (_calibrationPending)
                {
                    // blocked: waiting for session_start ACK before calibration can proceed
                }
                else
                {
                    _lastEnterTime = Time.time;
                    AdvanceStep();
                }
            }
        }

        // R key: retry calibration after FAIL
        bool r = kb.rKey.wasPressedThisFrame;
        if (r && _calibrationFailed && _oscSession != null
            && CurrentState == ExperimentState.Questionnaire
            && CurrentStepType == StepType.ConditionStart)
        {
            _calibrationFailed  = false;
            _calibrationPending = true;
            OnInstructionChanged?.Invoke(MessageBank.Get("calib.retrying"));
            _oscSession.StartCalibration();
        }

        if (del)
        {
            if (CurrentState == ExperimentState.TaskRunning || CurrentState == ExperimentState.WhiteNoise)
                ForceSkip();
            else if (CurrentState == ExperimentState.Questionnaire)
            {
                // Emergency: Worker is frozen on questionnaire — hide it and force advance.
                questionnaireManager?.Hide();
                AdvanceStep();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Experiment flow control
    // -----------------------------------------------------------------------

    private void StartExperiment()
    {
        if (steps.Count == 0) { UnityEngine.Debug.LogWarning("[ExperimentManager] No steps loaded."); return; }
        CurrentStepIndex = 0;
        ExecuteCurrentStep();
    }

    private IEnumerator OscReadyTimeout()
    {
        yield return new WaitForSeconds(10f);
        if (!_firstPongReceived)
        {
            Debug.LogWarning("[ExperimentManager2] Python OSC did not respond in 10 s — proceeding without OSC. Start Python or ignore if OSC is unused.");
            _firstPongReceived = true;
            _oscReadyTimeoutCo = null;
            TryTransitionToReady();
        }
    }

    /// <summary>
    /// True once the Expert's own setup prerequisites are met: instruction template loaded and
    /// (if an OSC/Python session exists) the first pong received. Computed locally — no network read.
    /// Used to gate the Setup approval button so the operator can't approve before their own side
    /// is ready (root cause #2 in the operator runbook). Meaningful on the Expert only.
    /// </summary>
    public bool IsExpertSelfReady => _templateLoaded && (_oscSession == null || _firstPongReceived);

    private void TryTransitionToReady()
    {
        if (!IsExpert) return;
        if (!_templateLoaded) return;
        if (_oscSession != null && !_firstPongReceived) return;
        if (CurrentState == ExperimentState.Idle)
            Transition(ExperimentState.Ready);
    }

    /// <summary>
    /// Expert calls this once Worker setup is verified — transitions Setup → Idle → Ready.
    /// </summary>
    public void TriggerSetupComplete()
    {
        if (!IsExpert) return;
        if (CurrentState != ExperimentState.Setup) return;
        Transition(ExperimentState.Idle);
        TryTransitionToReady();
    }

    private void HandleCalibrationResult(int quality, float errX, float errY)
    {
        FileLogger.Log("Experiment", $"[ExperimentManager2] CalibrationResult quality={quality} err=({errX:F3},{errY:F3})");
        _calibrationPending = false;
        if (quality >= 1) // PASS (2) or MARGINAL (1)
        {
            _calibrationFailed = false;
            string msg = quality == 2
                ? MessageBank.Get("calib.pass")
                : MessageBank.Format("calib.marginal", ("errX", errX.ToString("F3")), ("errY", errY.ToString("F3")));
            OnInstructionChanged?.Invoke(msg);
        }
        else // FAIL or aborted (quality == 0)
        {
            _calibrationFailed = true;
            OnInstructionChanged?.Invoke(MessageBank.Get("calib.fail"));
        }
    }

    private void AdvanceStep()
    {
        // Clear calibration gate whenever we advance (handles forced Del-skip during calibration)
        _calibrationPending = false;
        _calibrationFailed  = false;
        if (_pendingCalibAckHandler != null)
        {
            if (_oscSession != null) _oscSession.OnAck -= _pendingCalibAckHandler;
            _pendingCalibAckHandler = null;
        }

        // If a task timer is still running (e.g. Worker clicked Done early), stop it
        // and send EndTrial so Python and the state machine stay consistent.
        if (IsExpert && CurrentState == ExperimentState.TaskRunning)
        {
            if (CurrentStepType == StepType.Assembly) UnfollowWorker();
            _oscSession?.EndTrial();
            StopTimer();
        }

        CurrentStepIndex++;
        if (CurrentStepIndex >= steps.Count)
        {
            Transition(ExperimentState.Finished);
            return;
        }
        ExecuteCurrentStep();
    }

    // Guard wrappers that prevent double-advance from async callbacks
    // (e.g. Expert presses Enter, then Worker's Photon RPC also fires AdvanceStep).
    private void AdvanceStepFromTask()
    {
        if (CurrentState != ExperimentState.TaskRunning) return;
        AdvanceStep();
    }

    private void AdvanceStepFromQuestionnaire()
    {
        if (CurrentState != ExperimentState.Questionnaire) return;
        AdvanceStep();
    }

    private void ForceSkip()
    {
        StopTimer();
        StopNoiseRoutine();
        audioSource?.Stop();
        _natureSoundSource?.Stop();
        RemainingSeconds = 0f;
        OnTimerUpdated?.Invoke(0f);

        if (CurrentState == ExperimentState.TaskRunning)
        {
            if (IsExpert && CurrentStepType == StepType.Assembly) UnfollowWorker();
            _oscSession?.EndTrial();
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
                FileLogger.Log("Experiment", $"[ExperimentManager2] StartTrial: T{CurrentStepIndex}");
                _oscSession?.StartTrial($"T{CurrentStepIndex}");
                break;

            case StepType.Assembly:
                if (IsExpert) FollowWorker();
                RemainingSeconds = assemblyDurationSeconds;
                Transition(ExperimentState.TaskRunning);
                RestartTimerAt(assemblyDurationSeconds);
                FileLogger.Log("Experiment", $"[ExperimentManager2] StartTrial: A{CurrentStepIndex}");
                _oscSession?.StartTrial($"A{CurrentStepIndex}");
                break;

            case StepType.Alignment:
                if (IsExpert) AlignToWorker();
                Transition(ExperimentState.Questionnaire);
                break;

            case StepType.ConditionStart:
                _calibrationPending = false;
                _calibrationFailed  = false;
                if (IsExpert && step.ConditionIndex >= 0 && step.ConditionIndex < ExperimentDesign.Conditions.Length)
                {
                    int condIdx = step.ConditionIndex;
                    CurrentConditionIndex = condIdx;
                    CurrentConditionType  = ExperimentDesign.Conditions[condIdx].noise;
                    CurrentGazeMode       = ExperimentDesign.Conditions[condIdx].gaze;
                    CurrentConditionName  = ExperimentDesign.Conditions[condIdx].name;

                    for (int pos = 0; pos < conditionOrder.Length; pos++)
                    {
                        if (conditionOrder[pos] == condIdx) { CurrentConditionRunPosition = pos; break; }
                    }

                    var gazeMode = ExperimentDesign.ToVisualizationMode(ExperimentDesign.Conditions[condIdx].gaze);
                    FileLogger.Log("Experiment", $"[ExperimentManager2] GazeMode set: {gazeMode} cond={CurrentConditionIndex}");
                    SetGazeMode(gazeMode);

                    if (_oscSession != null)
                    {
                        if (_oscSessionActive) { _oscSession.EndSession(); _oscSessionActive = false; }
                        _oscSession.StartSession(participantId, CurrentConditionType.ToString());
                        _oscSessionActive = true;

                        if (CurrentConditionType == ConditionType.Webcam ||
                            CurrentConditionType == ConditionType.WebcamFiltered)
                        {
                            _calibrationPending = true;
                            string condNameCapture = CurrentConditionName;
                            _pendingCalibAckHandler = (cmd, status) =>
                            {
                                if (cmd != "session_start") return;
                                _oscSession.OnAck -= _pendingCalibAckHandler;
                                _pendingCalibAckHandler = null;
                                if (status == "ok")
                                {
                                    FileLogger.Log("Experiment", $"[ExperimentManager2] StartCalibration for {condNameCapture} (after session ACK)");
                                    _oscSession.StartCalibration();
                                }
                                else
                                {
                                    Debug.LogWarning($"[ExperimentManager2] session_start ack status={status} — skipping calibration");
                                    _calibrationPending = false;
                                }
                            };
                            _oscSession.OnAck += _pendingCalibAckHandler;
                        }
                    }
                }
                Transition(ExperimentState.Questionnaire);
                if (IsExpert && _calibrationPending)
                    OnInstructionChanged?.Invoke(MessageBank.Get("calib.running"));
                break;

            case StepType.Questionnaire:
                Transition(ExperimentState.Questionnaire);
                questionnaireManager?.ShowNASATLX(CurrentConditionIndex, CurrentConditionName);
                break;
        }
    }

    private IEnumerator AdvanceNextFrame()
    {
        yield return null;
        AdvanceStep();
    }

    // -----------------------------------------------------------------------
    // Timer helpers
    // -----------------------------------------------------------------------

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
            _oscSession?.EndTrial();
            Transition(ExperimentState.TaskComplete);
        }
    }

    private void StopNoiseRoutine()
    {
        if (noiseRoutine != null) { StopCoroutine(noiseRoutine); noiseRoutine = null; }
    }

    // -----------------------------------------------------------------------
    // White noise
    // -----------------------------------------------------------------------

    private void PlayWhiteNoise()
    {
        if (noiseClip == null) noiseClip = BuildBrownNoiseClip();
        audioSource.clip   = noiseClip;
        audioSource.volume = whiteNoiseVolume;
        audioSource.Play();

        if (_natureSoundSource != null && _natureSoundClip != null)
        {
            _natureSoundSource.clip   = _natureSoundClip;
            _natureSoundSource.volume = natureSoundVolume;
            _natureSoundSource.Play();
        }

        float dbfs = 20f * Mathf.Log10(Mathf.Max(whiteNoiseVolume, 1e-6f));
        FileLogger.Log("Experiment", $"[ExperimentManager2] Interval started. BrownNoise vol={whiteNoiseVolume:F2} ({dbfs:F1} dBFS), " +
                  $"NatureSound vol={natureSoundVolume:F2}, Duration={whiteNoiseDurationSeconds:F0}s. " +
                  "TARGET: calibrate to ~45 dBSPL at participant ear position with SPL meter.");
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
        _natureSoundSource?.Stop();
        OnTimerUpdated?.Invoke(0f);
        if (IsExpert) Transition(ExperimentState.NoiseComplete);
    }

    private AudioClip BuildBrownNoiseClip()
    {
        int     rate    = AudioSettings.outputSampleRate;
        int     samples = rate * 5;
        float[] data    = new float[samples];
        var    rng     = new System.Random();
        double prev    = 0.0;
        for (int i = 0; i < samples; i++)
        {
            double white = rng.NextDouble() * 2.0 - 1.0;
            prev    = (prev + 0.02 * white) / 1.02;
            data[i] = (float)(prev * 3.5);
        }
        var clip = AudioClip.Create("BrownNoise", samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // -----------------------------------------------------------------------
    // State machine / Photon broadcast
    // -----------------------------------------------------------------------

    private void Transition(ExperimentState next)
    {
        FileLogger.Log("Experiment", $"[ExperimentManager2] State: {CurrentState} → {next}");
        CurrentState = next;
        if (IsExpert) TotalSteps = steps.Count;
        if (next == ExperimentState.Finished)
        {
            if (_oscSessionActive) { _oscSession?.EndSession(); _oscSessionActive = false; }
            questionnaireManager?.ShowSSQ();
        }
        // Worker shows NASA-TLX when it receives the Questionnaire state for a Questionnaire step.
        // Expert's path goes through ExecuteCurrentStep instead (ShowNASATLX is a no-op for Expert).
        if (!IsExpert && next == ExperimentState.Questionnaire && CurrentStepType == StepType.Questionnaire)
            questionnaireManager?.ShowNASATLX(CurrentConditionIndex, CurrentConditionName);

        // Do not fire step instruction text for states that have their own fixed
        // messages in the UI. TaskComplete/NoiseComplete in particular must not
        // be overridden 窶・doing so would show the just-finished step's instruction
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
            else { audioSource.Stop(); _natureSoundSource?.Stop(); }

            // Mirror the Expert's streaming-mode toggle so the Worker's GazeVisualizer
            // uses the PCA camera FOV/aspect during Assembly instead of the PC camera FOV.
            bool streamingNeeded = next == ExperimentState.TaskRunning
                                   && CurrentStepType == StepType.Assembly;
            SetAllVisualizersStreamingMode(streamingNeeded);
        }
    }

    private void BroadcastState(ExperimentState state)
    {
        byte condByte    = CurrentConditionIndex    < 0 ? (byte)255 : (byte)CurrentConditionIndex;
        byte runPosByte  = CurrentConditionRunPosition < 0 ? (byte)255 : (byte)CurrentConditionRunPosition;
        object[] data =
        {
            (byte)state,
            (byte)Mathf.Clamp(CurrentStepIndex, 0, 255),
            (byte)Mathf.Clamp(TotalSteps,       0, 255),
            (byte)CurrentStepType,
            RemainingSeconds,
            condByte,   // data[5]: 255 = no active condition
            runPosByte  // data[6]: 255 = no active run position
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
            FileLogger.Log("Experiment", $"[ExperimentManager] Sync request sent (still Idle after {delay}s).");
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
            // Worker does not mirror Expert-only gate states
            if (newState == ExperimentState.Ready) return;
            int   syncedStep      = Convert.ToInt32(data[1]);
            int   syncedTotal     = Convert.ToInt32(data[2]);
            var   syncedType      = (StepType)Convert.ToByte(data[3]);
            float syncedRemaining = data.Length > 4 ? Convert.ToSingle(data[4]) : taskDurationSeconds;

            FileLogger.Log("Experiment", $"[ExperimentManager] OnEvent: state={newState}, step={syncedStep}/{syncedTotal}, remaining={syncedRemaining:F1}s");

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

            // Sync condition fields broadcast by Expert
            if (data.Length > 5)
            {
                int syncedCond = Convert.ToInt32(data[5]);
                if (syncedCond != 255 && syncedCond < ExperimentDesign.Conditions.Length)
                {
                    CurrentConditionIndex = syncedCond;
                    CurrentConditionType  = ExperimentDesign.Conditions[syncedCond].noise;
                    CurrentGazeMode       = ExperimentDesign.Conditions[syncedCond].gaze;
                    CurrentConditionName  = ExperimentDesign.Conditions[syncedCond].name;
                }
                else if (syncedCond == 255)
                {
                    CurrentConditionIndex = -1;
                }
            }
            if (data.Length > 6)
            {
                int syncedRunPos = Convert.ToInt32(data[6]);
                CurrentConditionRunPosition = syncedRunPos == 255 ? -1 : syncedRunPos;
            }

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

    // -----------------------------------------------------------------------
    // instructions.txt loading and template parsing
    // -----------------------------------------------------------------------

    private IEnumerator LoadInstructions()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "instructions_new.txt");

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

        if (conditionTemplate.Count == 0)
        {
            UnityEngine.Debug.LogError("[ExperimentManager] instructions_new.txt が見つからないか空です。実験を開始できません。");
            OnInstructionChanged?.Invoke(CoGazeStrings.Exp_InstructionsMissing);
            yield break;
        }

        ExpandTemplate();
        TotalSteps = steps.Count;
        _templateLoaded = true;
        if (IsExpert) TryTransitionToReady();
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

        FileLogger.Log("Experiment", $"[ExperimentManager] Template: {conditionTemplate.Count} steps/condition.");
    }

    private void BuildConditionOrder()
    {
        conditionOrder = ExperimentDesign.ComputeOrder(participantOrderIndex);
        FileLogger.Log("Experiment",
            $"[ExperimentManager] P{participantNumber} idx={participantOrderIndex} " +
            $"→ [{string.Join(", ", conditionOrder)}]");
    }

    // Expand template into 10 flat conditions (no blocking, no Launch steps).
    private void ExpandTemplate()
    {
        steps.Clear();
        int total = ExperimentDesign.Conditions.Length; // 10

        if (conditionOrder == null || conditionOrder.Length < total)
        {
            Debug.LogError($"[ExperimentManager] conditionOrder has {conditionOrder?.Length ?? 0} entries, expected {total}. Aborting ExpandTemplate.");
            return;
        }

        for (int c = 0; c < total; c++)
        {
            int condIdx = conditionOrder[c];
            var cond    = ExperimentDesign.Conditions[condIdx];

            // ConditionStart: switch gaze mode and open ready gate
            steps.Add(new ExperimentStep
            {
                Type             = StepType.ConditionStart,
                ConditionIndex   = condIdx,
                Instruction      = MessageBank.Format("step.condstart.expert",
                    ("pos", (c + 1).ToString()), ("total", total.ToString()), ("name", cond.name)),
                LocalInstruction = MessageBank.Format("step.condstart.worker",
                    ("pos", (c + 1).ToString()), ("total", total.ToString())),
            });

            // Per-condition steps from template
            foreach (var t in conditionTemplate)
            {
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

        FileLogger.Log("Experiment", $"[ExperimentManager] Expanded: {total} conditions × {conditionTemplate.Count} template steps = {steps.Count} total.");
    }

    // -----------------------------------------------------------------------
    // Worker / Expert positioning helpers
    // -----------------------------------------------------------------------

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
            FileLogger.Log("Experiment", $"[ExperimentManager] Expert aligned to Worker: pos={ph.transform.position}");
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
            FileLogger.Log("Experiment", "[ExperimentManager] Expert follow mode started.");
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
            FileLogger.Log("Experiment", "[ExperimentManager] Expert follow mode ended.");
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

    // -----------------------------------------------------------------------
    // Gaze mode helpers
    // -----------------------------------------------------------------------

    public void SetGazeHandler(GazeHandler handler)
    {
        expertGazeHandler = handler;
    }

    private void SetGazeMode(VisualizationMode mode)
    {
        if (expertGazeHandler == null) expertGazeHandler = GetComponent<GazeHandler>();
        if (expertGazeHandler != null)
        {
            expertGazeHandler.CurrentMode = mode;
            FileLogger.Log("Experiment", $"[ExperimentManager2] Gaze mode → {mode}");
        }
    }

    // -----------------------------------------------------------------------
    // Instruction text helpers
    // -----------------------------------------------------------------------

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
