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

// Drives a 10-condition (3 gaze modes × 3 noise) experiment; conditions and counterbalancing in ExperimentDesign.
public class ExperimentManager2 : MonoBehaviour, IOnEventCallback
{
    // -----------------------------------------------------------------------
    // Inspector fields
    // -----------------------------------------------------------------------

    [Header("Timings")]
    public float taskDurationSeconds       = 60f;
    public float assemblyDurationSeconds   = 180f;
    public float whiteNoiseDurationSeconds = 20f;
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
    // Identification correct-answer target for the current step ("" when none/out of range). Read by
    // ExperimentLogger; SNAPSHOT it at trial start — the step advances before the end-row is written.
    public string          CurrentTargetMarkerId =>
        (CurrentStepIndex >= 0 && CurrentStepIndex < _steps.Count)
            ? _steps[CurrentStepIndex].TargetMarkerId : string.Empty;
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
    // 3-2-1 countdown before the first condition; ticks 3→2→1→0(GO!) on Expert, Worker starts
    // its own local coroutine on the Setup→Idle state transition at roughly the same time.
    public event Action<int>                   OnCountdownTick;

    // Expose to ExpertUI2 for live target/score display
    public IdentificationTask IdentificationTask => _identificationTask;

    // -----------------------------------------------------------------------
    // Private fields
    // -----------------------------------------------------------------------

    private OscSessionManager _oscSession;
    private bool              _oscSessionActive;
    private bool              _templateLoaded;
    private bool              _firstPongReceived;
    private System.Action     _pongForReadyHandler;
    private Coroutine         _oscReadyTimeoutCo;
    private bool              _calibrationPending;
    private bool              _calibrationFailed;
    private System.Action<string, string> _pendingCalibAckHandler;
    private Coroutine         _calibTimeoutCo;          // covers session_start ACK + calibration result
    private WebcamCalibrationUI _webcamCalibUI;
    private bool              _ssqGateArmed; // Expert holds back Finished UI until SSQ submit
    private System.Action     _ssqSubmitHandler;
    private bool              _countdownFired;   // fires once before the very first condition
    private bool              _countdownRunning; // blocks Enter key during the 3-2-1 sequence

    private List<ExperimentStep> _steps = new();
    private List<ExperimentStep> _conditionTemplate = new();
    private string[]             _conditionTargets;   // per-condition identification target id (index = condition 0-9), from the "targets =" config line; null = untracked
    private int[]                _conditionOrder;   // WilliamsTable[participantOrderIndex]

    private AudioSource  _audioSource;
    private AudioSource  _natureSoundSource;
    private AudioClip    _noiseClip;
    private AudioClip    _natureSoundClip;
    private Coroutine    _timerRoutine;
    private Coroutine    _noiseRoutine;
    private Coroutine    _resyncRoutine;
    private GazeHandler  _expertGazeHandler;

    private QuestionnaireManager _questionnaireManager;
    private IdentificationTask   _identificationTask;

    private const byte k_photonEvent   = 43;
    private const byte k_syncRequest   = 0xFF;
    private const byte k_countdownTick = 0xFE;
    private const byte k_workerPaused  = 0xFD;
    private const byte k_expertNotice  = 0xFC;

    // Expert-activity notice codes sent via k_expertNotice. The webcam calibration runs entirely
    // on the Expert PC (calib.* messages fire only local OnInstructionChanged there), so without
    // an explicit broadcast the Worker waits ~1 minute on a generic "preparing" text with no clue
    // what is happening.
    private enum ExpertNotice : byte { CalibRunning = 0, CalibDone = 1, CalibIssue = 2 }

    private bool _workerPauseStoppedTimer;

    // -----------------------------------------------------------------------
    // Initialization
    // -----------------------------------------------------------------------

    public void Initialize(bool isExpert)
    {
        IsExpert = isExpert;
        CurrentState = ExperimentState.Setup;
        PhotonNetwork.AddCallbackTarget(this);

        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null) _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.spatialBlend = 0f;
        _audioSource.playOnAwake  = false;
        _audioSource.loop         = true;

        _natureSoundSource = gameObject.AddComponent<AudioSource>();
        _natureSoundSource.spatialBlend = 0f;
        _natureSoundSource.playOnAwake  = false;
        _natureSoundSource.loop         = true;
        _natureSoundClip = Resources.Load<AudioClip>("Audio/rain_loop");
        if (_natureSoundClip == null)
            FileLogger.Log("Experiment", "[ExperimentManager2] Nature sound not found at Resources/Audio/rain_loop — brown noise only. Add a looping rain WAV to Assets/Resources/Audio/rain_loop.wav to enable.");
        else
            FileLogger.Log("Experiment", $"[ExperimentManager2] Nature sound loaded: {_natureSoundClip.name}");

        // _expertGazeHandler is injected via SetGazeHandler() before Initialize() is called.
        // Do NOT call GetComponent<GazeHandler>() here — GazeHandler lives on the player
        // prefab, not on this GameObject, so it would return null and overwrite the injected ref.

        StartCoroutine(LoadInstructions());

        if (!isExpert)
            StartCoroutine(DelaySyncRequest());

        _questionnaireManager = FindAnyObjectByType<QuestionnaireManager>();
        if (_questionnaireManager != null)
        {
            _questionnaireManager.participantId     = participantId;
            _questionnaireManager.participantNumber = participantOrderIndex;
            if (isExpert) _questionnaireManager.OnQuestionnaireComplete += AdvanceStepFromQuestionnaire;
        }
        _identificationTask = FindAnyObjectByType<IdentificationTask>();
        // IdentificationTask no longer drives AdvanceStep — the per-task timer does.
        // Workers answer repeatedly until the timer expires, then Enter advances the step.

        if (isExpert)
        {
            _oscSession = FindAnyObjectByType<OscSessionManager>();
            if (_oscSession != null)
            {
                FileLogger.Log("Experiment", "[ExperimentManager] OscSessionManager found — Python OSC enabled.");
                _oscSession.OnCalibrationResult += HandleCalibrationResult;
                _oscSession.OnCalibrationRetrying += _ =>
                {
                    _webcamCalibUI?.StartCalibration();
                    BroadcastExpertNotice(ExpertNotice.CalibRunning);
                };

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
            if (_calibTimeoutCo != null) { StopCoroutine(_calibTimeoutCo); _calibTimeoutCo = null; }
            if (_questionnaireManager != null) _questionnaireManager.OnQuestionnaireComplete -= AdvanceStepFromQuestionnaire;
            if (_ssqSubmitHandler != null && _questionnaireManager != null)
            {
                _questionnaireManager.OnSurveySubmitted -= _ssqSubmitHandler;
                _ssqSubmitHandler = null;
            }
            // _identificationTask.OnTaskComplete is no longer wired to AdvanceStepFromTask
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
        if (_noiseClip != null) Destroy(_noiseClip);
    }

    // -----------------------------------------------------------------------
    // Expert keyboard input
    // -----------------------------------------------------------------------

    private float _lastEnterTime = -1f;

    // Del is destructive (force-skip a trial / force-advance a frozen questionnaire); require a
    // deliberate hold rather than a single tap so an accidental keypress can't destroy a trial.
    private const float k_delHoldSeconds = 0.8f;
    private float       _delHeldSince  = -1f;
    private bool        _delFired;

    private void Update()
    {
        if (!IsExpert) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        bool enter   = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
        bool delHeld = kb.deleteKey.isPressed;

        // 500ms debounce prevents accidental double-advance from a single key bounce.
        if (enter && Time.time - _lastEnterTime > 0.5f)
        {
            if (CurrentState == ExperimentState.Tutorial)
            {
                _lastEnterTime = Time.time;
                TriggerTutorialComplete();
            }
            else if (CurrentState == ExperimentState.Ready && !_countdownRunning)
            {
                _lastEnterTime = Time.time;
                if (!_countdownFired)
                {
                    _countdownFired = true;
                    StartCoroutine(CountdownThenStartExperiment());
                }
                else
                {
                    StartExperiment();
                }
            }
            else if (CurrentState == ExperimentState.TaskComplete  ||
                     CurrentState == ExperimentState.NoiseComplete ||
                     CurrentState == ExperimentState.Questionnaire)
            {
                // A NASA-TLX / SSQ questionnaire (StepType.Questionnaire) must advance ONLY on its own
                // submit (OnQuestionnaireComplete) — pressing Enter here would truncate the Worker's
                // in-progress answers. The Questionnaire *state* is also reused by the operator-gated
                // steps (ConditionStart / Alignment / Rest), which DO advance on Enter, so gate on the
                // StepType rather than the state.
                bool answerableQuestionnaire =
                    CurrentState == ExperimentState.Questionnaire &&
                    CurrentStepType == StepType.Questionnaire;

                if (_calibrationPending)
                {
                    // blocked: waiting for session_start ACK before calibration can proceed
                }
                else if (!answerableQuestionnaire)
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
            if (_calibTimeoutCo != null) StopCoroutine(_calibTimeoutCo);
            _calibTimeoutCo = StartCoroutine(CalibrationTimeout());
            OnInstructionChanged?.Invoke(MessageBank.Get("calib.retrying"));
            _webcamCalibUI?.StartCalibration();
            BroadcastExpertNotice(ExpertNotice.CalibRunning);
        }

        // Hold-to-confirm: fire once when Del has been held continuously for k_delHoldSeconds.
        if (delHeld)
        {
            if (_delHeldSince < 0f) _delHeldSince = Time.time;
            if (!_delFired && Time.time - _delHeldSince >= k_delHoldSeconds)
            {
                _delFired = true;
                if (CurrentState == ExperimentState.TaskRunning || CurrentState == ExperimentState.WhiteNoise)
                    ForceSkip();
                else if (CurrentState == ExperimentState.Finished && _ssqGateArmed)
                {
                    // Emergency: the final SSQ is not being submitted (e.g. Worker frozen) — release
                    // the gate and reveal the Finished/thanks UI so the operator is not stuck forever.
                    _questionnaireManager?.Hide();
                    RevealFinished();
                }
                else if (CurrentState == ExperimentState.Questionnaire)
                {
                    // Emergency: Worker is frozen on questionnaire — hide it and force advance.
                    _questionnaireManager?.Hide();
                    AdvanceStep();
                }
            }
        }
        else { _delHeldSince = -1f; _delFired = false; }
    }

    // -----------------------------------------------------------------------
    // Experiment flow control
    // -----------------------------------------------------------------------

    private void StartExperiment()
    {
        if (_steps.Count == 0) { UnityEngine.Debug.LogWarning("[ExperimentManager] No steps loaded."); return; }
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

    public bool IsExpertSelfReady => _templateLoaded && (_oscSession == null || _firstPongReceived);

    private void TryTransitionToReady()
    {
        if (!IsExpert) return;
        if (!_templateLoaded) return;
        if (_oscSession != null && !_firstPongReceived) return;
        if (CurrentState == ExperimentState.Idle)
            Transition(ExperimentState.Ready);
    }

    public void TriggerSetupComplete()
    {
        if (!IsExpert) return;
        if (CurrentState != ExperimentState.Setup) return;
        // Python が未応答なら自動起動を試みる（起動後は既存の OscReadyTimeout が pong を待つ）
        if (_oscSession != null && !_firstPongReceived)
            _oscSession.TryAutoLaunchPython();
        Transition(ExperimentState.Tutorial);
    }

    public void TriggerTutorialComplete()
    {
        if (!IsExpert) return;
        if (CurrentState != ExperimentState.Tutorial) return;
        Transition(ExperimentState.Idle);
        TryTransitionToReady();
    }

    private IEnumerator CountdownThenStartExperiment()
    {
        _countdownRunning = true;
        int[]   ticks = { 3, 2, 1, 0 };
        float[] waits = { 1f, 1f, 1f, 0.5f };
        for (int i = 0; i < ticks.Length; i++)
        {
            OnCountdownTick?.Invoke(ticks[i]);
            BroadcastCountdownTick(ticks[i]);
            yield return new WaitForSeconds(waits[i]);
        }
        _countdownRunning = false;
        StartExperiment();
    }

    private void BroadcastCountdownTick(int tick)
    {
        object[] data = { k_countdownTick, (byte)tick };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(k_photonEvent, data, opts, SendOptions.SendReliable);
    }

    private void HandleCalibrationResult(int quality, float errX, float errY)
    {
        FileLogger.Log("Experiment", $"[ExperimentManager2] CalibrationResult quality={quality} err=({errX:F3},{errY:F3})");
        FindAnyObjectByType<ExperimentLogger>()?.SetCalibResult(quality, errX, errY);
        _calibrationPending = false;
        if (_calibTimeoutCo != null) { StopCoroutine(_calibTimeoutCo); _calibTimeoutCo = null; }
        if (quality >= 1) // PASS (2) or MARGINAL (1)
        {
            _calibrationFailed = false;
            string msg = quality == 2
                ? MessageBank.Get("calib.pass")
                : MessageBank.Format("calib.marginal", ("errX", errX.ToString("F3")), ("errY", errY.ToString("F3")));
            OnInstructionChanged?.Invoke(msg);
            BroadcastExpertNotice(ExpertNotice.CalibDone);
        }
        else // FAIL or aborted (quality == 0)
        {
            _calibrationFailed = true;
            OnInstructionChanged?.Invoke(MessageBank.Get("calib.fail"));
            BroadcastExpertNotice(ExpertNotice.CalibIssue);
        }
    }

    private void AdvanceStep()
    {
        // Clear calibration gate whenever we advance (handles forced Del-skip during calibration)
        _calibrationPending = false;
        _calibrationFailed  = false;
        if (_calibTimeoutCo != null) { StopCoroutine(_calibTimeoutCo); _calibTimeoutCo = null; }
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
        if (CurrentStepIndex >= _steps.Count)
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
        _audioSource?.Stop();
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
        var step = _steps[CurrentStepIndex];
        CurrentStepType = step.Type;

        switch (step.Type)
        {
            case StepType.Noise:
                RemainingSeconds = whiteNoiseDurationSeconds;
                Transition(ExperimentState.WhiteNoise);
                PlayWhiteNoise();
                StopNoiseRoutine();
                _noiseRoutine = StartCoroutine(NoiseDuration());
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

            case StepType.Rest:
                // Reuse the Questionnaire gate state: no questionnaire UI is shown (that happens only
                // for StepType.Questionnaire), the rest instruction is broadcast via Transition, and
                // the Enter handler advances on Questionnaire state — so Enter resumes the experiment.
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

                    for (int pos = 0; pos < _conditionOrder.Length; pos++)
                    {
                        if (_conditionOrder[pos] == condIdx) { CurrentConditionRunPosition = pos; break; }
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
                            if (_calibTimeoutCo != null) StopCoroutine(_calibTimeoutCo);
                            _calibTimeoutCo = StartCoroutine(CalibrationTimeout());
                            string condNameCapture = CurrentConditionName;
                            _pendingCalibAckHandler = (cmd, status) =>
                            {
                                if (cmd != "session_start") return;
                                _oscSession.OnAck -= _pendingCalibAckHandler;
                                _pendingCalibAckHandler = null;
                                if (status == "ok")
                                {
                                    FileLogger.Log("Experiment", $"[ExperimentManager2] StartCalibration for {condNameCapture} (after session ACK)");
                                    _webcamCalibUI?.StartCalibration();
                                    BroadcastExpertNotice(ExpertNotice.CalibRunning);
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
                _questionnaireManager?.ShowNASATLX(CurrentConditionIndex, CurrentConditionName);
                break;

            default:
                // Safety net: an unhandled StepType (e.g. StepType.Launch, which ExpandTemplate does
                // not currently generate) must not silently stall the run. Log it and skip to the next
                // step on the following frame rather than falling through and doing nothing.
                FileLogger.Log("Experiment", $"[ExperimentManager2] Unhandled StepType {step.Type} at step {CurrentStepIndex} — skipping to next step.");
                StartCoroutine(AdvanceNextFrame());
                break;
        }
    }

    private IEnumerator AdvanceNextFrame()
    {
        yield return null;
        AdvanceStep();
    }

    // Subscribe to the SSQ submit on the NEXT frame so we do not catch the submit event that is
    // firing right now (the final NASA-TLX whose completion triggered this Finished transition);
    // the next OnSurveySubmitted after that is the SSQ submit. Expert only.
    private IEnumerator ArmSsqGate()
    {
        yield return null;
        if (!_ssqGateArmed || _questionnaireManager == null) yield break;
        _ssqSubmitHandler = () => RevealFinished();
        _questionnaireManager.OnSurveySubmitted += _ssqSubmitHandler;
    }

    private void RevealFinished()
    {
        if (!_ssqGateArmed) return;        // already revealed
        _ssqGateArmed = false;
        if (_ssqSubmitHandler != null && _questionnaireManager != null)
            _questionnaireManager.OnSurveySubmitted -= _ssqSubmitHandler;
        _ssqSubmitHandler = null;

        FileLogger.Log("Experiment", "[ExperimentManager2] SSQ submitted — revealing Finished UI.");
        OnStateChanged?.Invoke(ExperimentState.Finished);
        OnProgressChanged?.Invoke(CurrentStepIndex, TotalSteps, CurrentStepType);
    }

    // Covers BOTH the session_start ACK wait and the subsequent calibration-result wait. If neither
    // arrives, the Enter key stays blocked forever (silent cross-process deadlock). On timeout, surface
    // an operator-visible failure and a recovery path: [R] re-sends calibration, [Enter] skips, [Del]
    // force-advances.
    private IEnumerator CalibrationTimeout()
    {
        const float CalibTimeoutSeconds = 90f;  // 16-dot Unity UI: ~2s instr + 16×2.8s ≈ 47s total
        float elapsed = 0f;
        while (elapsed < CalibTimeoutSeconds)
        {
            if (!_calibrationPending) { _calibTimeoutCo = null; yield break; }
            elapsed += Time.deltaTime;
            yield return null;
        }
        _calibTimeoutCo = null;
        if (!_calibrationPending) yield break;   // resolved on the final frame

        // Timed out while still pending — unblock the operator and allow retry.
        _calibrationPending = false;
        _calibrationFailed  = true;              // enables the [R] retry path
        if (_pendingCalibAckHandler != null)
        {
            if (_oscSession != null) _oscSession.OnAck -= _pendingCalibAckHandler;
            _pendingCalibAckHandler = null;
        }
        FileLogger.Log("Experiment", $"[ExperimentManager2] Calibration timed out after {CalibTimeoutSeconds:F0}s (no session_start ACK or result).");
        OnInstructionChanged?.Invoke(
            "キャリブレーションが応答しません (タイムアウト)。\nPython プロセスの状態を確認してください。\n[R] で再試行 / [Enter] でスキップ");
        BroadcastExpertNotice(ExpertNotice.CalibIssue);
    }

    // -----------------------------------------------------------------------
    // Timer helpers
    // -----------------------------------------------------------------------

    public void RestartTimerAt(float startSeconds)
    {
        StopTimer();
        RemainingSeconds = Mathf.Max(0f, startSeconds);
        _timerRoutine = StartCoroutine(RunTimer());
    }

    private void StopTimer()
    {
        if (_timerRoutine != null) { StopCoroutine(_timerRoutine); _timerRoutine = null; }
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
        if (_noiseRoutine != null) { StopCoroutine(_noiseRoutine); _noiseRoutine = null; }
    }

    // -----------------------------------------------------------------------
    // White noise
    // -----------------------------------------------------------------------

    private void PlayWhiteNoise()
    {
        if (_noiseClip == null) _noiseClip = BuildBrownNoiseClip();
        _audioSource.clip   = _noiseClip;
        _audioSource.volume = whiteNoiseVolume;
        _audioSource.Play();

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
        _audioSource.Stop();
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
        if (IsExpert) TotalSteps = _steps.Count;
        if (next == ExperimentState.Finished)
        {
            if (_oscSessionActive) { _oscSession?.EndSession(); _oscSessionActive = false; }
            _questionnaireManager?.ShowSSQ();

            // SSQ-completion gate: the Worker is now answering the final SSQ. On the Expert, hold back
            // the "finished/thanks" UI until the Worker submits the SSQ — otherwise the operator may end
            // the session early and the final SSQ measure is silently lost. The Worker still shows the
            // SSQ (ShowSSQ above) and its own thanks HUD stays behind the SSQ panel until the panel
            // closes on submit, so it is naturally gated too. RevealFinished() fires the deferred UI.
            if (IsExpert && !_ssqGateArmed && _questionnaireManager != null)
            {
                _ssqGateArmed = true;
                StartCoroutine(ArmSsqGate());
            }
        }
        // Worker shows NASA-TLX when it receives the Questionnaire state for a Questionnaire step.
        // Expert's path goes through ExecuteCurrentStep instead (ShowNASATLX is a no-op for Expert).
        if (!IsExpert && next == ExperimentState.Questionnaire && CurrentStepType == StepType.Questionnaire)
            _questionnaireManager?.ShowNASATLX(CurrentConditionIndex, CurrentConditionName);

        // Do not fire step instruction text for states that have their own fixed
        // messages in the UI. TaskComplete/NoiseComplete in particular must not
        // be overridden 窶・doing so would show the just-finished step's instruction
        // one beat late instead of the "task ended, press Enter" message.
        bool suppressInstruction = next == ExperimentState.Ready
                                || next == ExperimentState.Idle
                                || next == ExperimentState.Tutorial
                                || next == ExperimentState.Finished
                                || next == ExperimentState.TaskComplete
                                || next == ExperimentState.NoiseComplete;
        string instruction = suppressInstruction ? string.Empty : GetCurrentInstruction();

        // While the SSQ gate is armed (Expert only), withhold the Finished/thanks UI and show the
        // operator a "waiting for SSQ" message instead. RevealFinished() fires the held-back events
        // once the Worker submits the SSQ (or the operator force-releases via Del-hold).
        bool ssqGateDeferred = IsExpert && _ssqGateArmed && next == ExperimentState.Finished;
        if (ssqGateDeferred)
        {
            OnInstructionChanged?.Invoke(
                "Worker が最終アンケート (SSQ) に回答しています。\n回答が完了するまでお待ちください。");
        }
        else
        {
            OnStateChanged?.Invoke(next);
            OnInstructionChanged?.Invoke(instruction);
            OnProgressChanged?.Invoke(CurrentStepIndex, TotalSteps, CurrentStepType);
        }

        if (IsExpert)
        {
            BroadcastState(next);
            StopResync();
            if (next == ExperimentState.TaskRunning || next == ExperimentState.WhiteNoise)
                _resyncRoutine = StartCoroutine(PeriodicResync());
        }

        if (!IsExpert)
        {
            if (next == ExperimentState.WhiteNoise) PlayWhiteNoise();
            else { _audioSource.Stop(); _natureSoundSource?.Stop(); }

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
        PhotonNetwork.RaiseEvent(k_photonEvent, data, opts, SendOptions.SendReliable);
    }

    public void BroadcastCurrentState() => BroadcastState(CurrentState);

    private void BroadcastExpertNotice(ExpertNotice notice)
    {
        if (!IsExpert) return;
        object[] data = { k_expertNotice, (byte)notice };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(k_photonEvent, data, opts, SendOptions.SendReliable);
    }

    public void SendSyncRequest()
    {
        if (!PhotonNetwork.InRoom) return; // Quest復帰直後など、まだルームにいない場合は無視
        object[] req = { k_syncRequest };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(k_photonEvent, req, opts, SendOptions.SendReliable);
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

    // Called when Quest goes to background (doffed) or returns to foreground.
    // On resume, always send a k_syncRequest so Expert re-broadcasts current state.
    // On pause, notify Expert so it can pause the task timer.
    private void OnApplicationPause(bool paused)
    {
        if (IsExpert) return;
        FileLogger.Log("Experiment", $"[ExperimentManager2] OnApplicationPause(paused={paused})");
        if (!paused)
        {
            StartCoroutine(ResumeResync());
        }
        else
        {
            try
            {
                object[] data = { k_workerPaused };
                var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
                PhotonNetwork.RaiseEvent(k_photonEvent, data, opts, SendOptions.SendReliable);
            }
            catch { /* ignore if Photon not connected */ }
        }
    }

    private IEnumerator ResumeResync()
    {
        // Quest復帰直後はPhoton再接続中のことがある。最大10秒待ってルーム復帰を確認してから送信。
        float timeout = 10f;
        while (!PhotonNetwork.InRoom && timeout > 0f)
        {
            yield return new WaitForSeconds(0.5f);
            timeout -= 0.5f;
        }
        if (!PhotonNetwork.InRoom) yield break;

        SendSyncRequest();
        FileLogger.Log("Experiment", "[ExperimentManager2] Resume sync request sent.");
        yield return new WaitForSeconds(2f);
        SendSyncRequest(); // retry in case the first was dropped
    }

    private void StopResync()
    {
        if (_resyncRoutine != null) { StopCoroutine(_resyncRoutine); _resyncRoutine = null; }
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
        _resyncRoutine = null;
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != k_photonEvent) return;

        try
        {
            object[] data = (object[])photonEvent.CustomData;
            byte first = Convert.ToByte(data[0]);

            if (first == k_syncRequest)
            {
                if (IsExpert)
                {
                    if (_workerPauseStoppedTimer && CurrentState == ExperimentState.TaskRunning)
                    {
                        _workerPauseStoppedTimer = false;
                        RestartTimerAt(RemainingSeconds);
                        FileLogger.Log("Experiment", "[ExperimentManager2] Worker resumed — timer restarted.");
                    }
                    BroadcastState(CurrentState);
                    // Re-send the calibration notice too: BroadcastState makes the resuming Worker
                    // show the generic ConditionStart text, which would silently replace the
                    // "calibration in progress" message mid-calibration.
                    if (_calibrationPending)
                        BroadcastExpertNotice(ExpertNotice.CalibRunning);
                }
                return;
            }

            if (first == k_workerPaused)
            {
                if (IsExpert)
                {
                    FileLogger.Log("Experiment", "[ExperimentManager2] Worker HMD doffed.");
                    if (CurrentState == ExperimentState.TaskRunning)
                    {
                        _workerPauseStoppedTimer = true;
                        StopTimer();
                    }
                    OnInstructionChanged?.Invoke("⚠ Worker の HMD が外されました。装着後、自動で再開します。");
                }
                return;
            }

            // Expert-activity notice (e.g. webcam calibration progress) — show on the Worker HUD
            // so the subject knows what the Expert is doing during otherwise-silent waits.
            if (first == k_expertNotice)
            {
                if (!IsExpert && data.Length > 1)
                {
                    switch ((ExpertNotice)Convert.ToByte(data[1]))
                    {
                        case ExpertNotice.CalibRunning:
                            OnInstructionChanged?.Invoke(CoGazeStrings.Worker_ExpertCalibrating);
                            break;
                        case ExpertNotice.CalibDone:
                            OnInstructionChanged?.Invoke(CoGazeStrings.Worker_ExpertCalibDone);
                            break;
                        case ExpertNotice.CalibIssue:
                            OnInstructionChanged?.Invoke(CoGazeStrings.Worker_ExpertCalibIssue);
                            break;
                    }
                }
                return;
            }

            // Countdown tick from Expert to Worker — fire the local event so WorkerHUD2 shows 3-2-1-GO!
            if (first == k_countdownTick)
            {
                if (!IsExpert)
                {
                    int tick = data.Length > 1 ? Convert.ToInt32(data[1]) : 0;
                    OnCountdownTick?.Invoke(tick);
                }
                return;
            }

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
                    _noiseRoutine = StartCoroutine(WorkerNoiseMirror(syncedRemaining));
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
                _noiseRoutine = StartCoroutine(WorkerNoiseMirror(syncedRemaining));
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
        _noiseRoutine = null;
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

        if (_conditionTemplate.Count == 0)
        {
            UnityEngine.Debug.LogError("[ExperimentManager] instructions_new.txt が見つからないか空です。実験を開始できません。");
            OnInstructionChanged?.Invoke(CoGazeStrings.Exp_InstructionsMissing);
            yield break;
        }

        ExpandTemplate();
        TotalSteps = _steps.Count;
        _templateLoaded = true;
        if (IsExpert) TryTransitionToReady();
    }

    private void ParseTemplate(string text)
    {
        _conditionTemplate.Clear();
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

            _conditionTemplate.Add(step);
            block.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;
            // Global directive: per-condition identification correct-answer targets, e.g.
            // "targets = A, B, C, A, ...". Parsed here (not as a step block) so it can sit anywhere.
            if (line.StartsWith("targets", System.StringComparison.OrdinalIgnoreCase) && line.Contains("="))
            {
                string[] parts = line.Substring(line.IndexOf('=') + 1).Split(',');
                _conditionTargets = new string[parts.Length];
                for (int i = 0; i < parts.Length; i++) _conditionTargets[i] = parts[i].Trim();
                continue;
            }
            if (line == "===") Commit();
            else block.Add(line);
        }
        Commit();

        FileLogger.Log("Experiment", $"[ExperimentManager] Template: {_conditionTemplate.Count} steps/condition.");
    }

    private void BuildConditionOrder()
    {
        _conditionOrder = ExperimentDesign.ComputeOrder(participantOrderIndex);
        FileLogger.Log("Experiment",
            $"[ExperimentManager] P{participantNumber} idx={participantOrderIndex} " +
            $"→ [{string.Join(", ", _conditionOrder)}]");
    }

    // Expand template into 10 flat conditions (no blocking, no Launch steps).
    private void ExpandTemplate()
    {
        _steps.Clear();
        int total = ExperimentDesign.Conditions.Length; // 10

        if (_conditionOrder == null || _conditionOrder.Length < total)
        {
            Debug.LogError($"[ExperimentManager] _conditionOrder has {_conditionOrder?.Length ?? 0} entries, expected {total}. Aborting ExpandTemplate.");
            return;
        }

        const int restEveryNConditions = 3; // insert a break every 3 conditions (≈ every 3-4 sessions)

        for (int c = 0; c < total; c++)
        {
            // Operator-gated rest break BETWEEN conditions (not before the first). Modeled on the
            // Alignment gate: no timer, Enter resumes. Worker sees the rest instruction text.
            if (c > 0 && c % restEveryNConditions == 0)
            {
                _steps.Add(new ExperimentStep
                {
                    Type             = StepType.Rest,
                    Instruction      = CoGazeStrings.Rest_Expert,
                    LocalInstruction = CoGazeStrings.Rest_Worker,
                });
            }

            int condIdx = _conditionOrder[c];
            var cond    = ExperimentDesign.Conditions[condIdx];

            // ConditionStart: switch gaze mode and open ready gate
            _steps.Add(new ExperimentStep
            {
                Type             = StepType.ConditionStart,
                ConditionIndex   = condIdx,
                Instruction      = MessageBank.Format("step.condstart.expert",
                    ("pos", (c + 1).ToString()), ("total", total.ToString()), ("name", cond.name)),
                LocalInstruction = CoGazeStrings.Exp_CondStartWorker(c + 1, total),
            });

            // Per-condition steps from template. Identification (Task) steps get this condition's
            // correct-answer target so the Expert can direct the Worker and the logger can score it.
            string condTarget = (_conditionTargets != null && condIdx < _conditionTargets.Length)
                ? _conditionTargets[condIdx] : string.Empty;
            foreach (var t in _conditionTemplate)
            {
                _steps.Add(new ExperimentStep
                {
                    Type             = t.Type,
                    Instruction      = t.Instruction,
                    LocalInstruction = t.LocalInstruction,
                    ScriptArgs       = t.ScriptArgs,
                    ConditionIndex   = -1,
                    TargetMarkerId   = t.Type == StepType.Task ? condTarget : string.Empty
                });
            }
        }

        // Log the resolved condition→identification-target map once so the researcher can verify the
        // config (it bakes a research-validity decision into a config line). Warn on a length mismatch —
        // a silently short/over list would leave some conditions untracked.
        if (_conditionTargets != null)
        {
            if (_conditionTargets.Length != total)
                FileLogger.Log("Experiment", $"[ExperimentManager] WARNING: 'targets' has {_conditionTargets.Length} entries, expected {total} (one per condition). Out-of-range conditions → untracked.");
            var sb = new System.Text.StringBuilder("[ExperimentManager] Identification targets (presentation order): ");
            for (int c = 0; c < total; c++)
            {
                int ci = _conditionOrder[c];
                string tgt = (ci < _conditionTargets.Length && !string.IsNullOrEmpty(_conditionTargets[ci]))
                    ? _conditionTargets[ci] : "(none)";
                sb.Append($"#{c + 1}={ExperimentDesign.Conditions[ci].name}:{tgt}  ");
            }
            FileLogger.Log("Experiment", sb.ToString().TrimEnd());
        }

        FileLogger.Log("Experiment", $"[ExperimentManager] Expanded: {total} conditions × {_conditionTemplate.Count} template steps = {_steps.Count} total.");
    }

    // -----------------------------------------------------------------------
    // Worker / Expert positioning helpers
    // -----------------------------------------------------------------------

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
        _expertGazeHandler = handler;
    }

    public void SetWebcamCalibUI(WebcamCalibrationUI ui)
    {
        _webcamCalibUI = ui;
    }

    private void SetGazeMode(VisualizationMode mode)
    {
        if (_expertGazeHandler == null) _expertGazeHandler = GetComponent<GazeHandler>();
        if (_expertGazeHandler != null)
        {
            _expertGazeHandler.CurrentMode = mode;
            FileLogger.Log("Experiment", $"[ExperimentManager2] Gaze mode → {mode}");
        }
    }

    // -----------------------------------------------------------------------
    // Instruction text helpers
    // -----------------------------------------------------------------------

    private string GetCurrentInstruction()
    {
        if (_steps == null || CurrentStepIndex >= _steps.Count) return string.Empty;
        return ResolveInstruction(_steps[CurrentStepIndex]);
    }

    public string GetInstruction(int idx)
    {
        if (_steps == null || idx >= _steps.Count) return string.Empty;
        return ResolveInstruction(_steps[idx]);
    }

    // Expert-facing NoGaze (control) instructions. CoGazeStrings has Worker NoGaze variants but no
    // Expert ones, so these gaze-neutral lines are defined here. Per the researcher's intent the
    // NoGaze control is "voice-only direction": the Expert still actively guides the Worker, but by
    // SPEECH only — gaze is not shared. The operator must NOT be told to "視線で示す".
    private const string k_noGazeTaskExpert =
        "識別課題 (視線共有なし・統制条件):\n口頭のみで正解の QR マーカーを Worker に指示してください。視線は共有されません。段を上げる毎にF2/F3で記録。";
    private const string k_noGazeAssemblyExpert =
        "組み立て課題 (視線共有なし・統制条件):\n口頭のみで配置位置を Worker に指示してください。視線は共有されません。段を上げる毎にF2/F3/F4で記録（F4=向き）。";

    private string ResolveInstruction(ExperimentStep step)
    {
        string text;
        if (CurrentGazeMode == GazeMode.None && step.Type == StepType.Task)
            text = IsExpert ? k_noGazeTaskExpert : CoGazeStrings.Worker_TaskNoGaze;
        else if (CurrentGazeMode == GazeMode.None && step.Type == StepType.Assembly)
            text = IsExpert ? k_noGazeAssemblyExpert : CoGazeStrings.Worker_AssemblyNoGaze;
        else
            text = IsExpert ? step.Instruction : step.LocalInstruction;

        // Show the identification correct-answer target to the EXPERT only, for BOTH gaze and NoGaze
        // (in NoGaze the Expert directs by voice and still needs to know which marker is correct). The
        // Worker must never see it. Empty target = untracked → no append.
        if (IsExpert && step.Type == StepType.Task && !string.IsNullOrEmpty(step.TargetMarkerId))
            text += $"\n【正解ターゲット: {step.TargetMarkerId}】";

        return text;
    }
}
