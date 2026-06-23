using System.Collections;
using UnityEngine;
using extOSC;

/// <summary>
/// OSC bridge between Unity (Expert PC) and the Python eye-tracking process.
///
/// Send port  : 9001  (Unity → Python)
/// Receive port: 9000 (Python → Unity)
///
/// IMPORTANT — port 9000 is shared with OscGazeInput (OscGazeInput.cs).
/// extOSC binds a UDP socket per OSCReceiver instance; opening a second
/// OSCReceiver on 9000 causes a SocketException at startup.
/// Solution: wire the *same* OSCReceiver that OscGazeInput owns into the
/// SharedReceiver field in the Inspector, OR leave it null and receiver setup
/// is deferred by one frame so OscGazeInput.Awake() (which creates the shared
/// receiver) always runs first — even when the component is added dynamically.
///
/// Condition flow (for reference):
///   IR / NoGaze : StartSession → ACK → StartTrial immediately.
///   Webcam / WebcamFiltered:
///       StartSession → ACK
///       → wait for OnFaceMetrics (status == 2 = good)
///       → StartCalibration → wait for OnCalibrationResult
///       → StartTrial.
///   Role check is the caller's responsibility; this component is always present
///   but ExperimentManager should only call its public API on the Expert (PC) side.
/// </summary>
public class OscSessionManager : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("OSC — Send (Unity → Python)")]
    [SerializeField] private string pythonHost = "127.0.0.1";
    [SerializeField] private int sendPort = 9001;

    [Header("OSC — Receive (Python → Unity, port 9000)")]
    [Tooltip("Assign the OSCReceiver that OscGazeInput owns. "
           + "Port 9000 can only have one UDP socket — do not create a second one.")]
    [SerializeField] private OSCReceiver sharedReceiver;

    [Header("Calibration Retry")]
    [Tooltip("Maximum automatic retries when calibration/result quality = MARGINAL (1).")]
    [SerializeField] private int maxMarginalRetries = 2;

    // ── Public events ─────────────────────────────────────────────────────────

    /// <summary>Fired when Python sends /calibration/result (terminal result only — after retries).</summary>
    public event System.Action<int, float, float> OnCalibrationResult;   // quality, err_x, err_y

    /// <summary>Fired on every /face/metrics message. status: 0=noface 1=toofar 2=good 3=tooclose.</summary>
    public event System.Action<float, float, float, int> OnFaceMetrics;  // iod_norm, cx, cy, status

    /// <summary>Fired when Python acknowledges a command via /experiment/ack.</summary>
    public event System.Action<string, string> OnAck;                    // command, status

    /// <summary>Fired when Python replies to a /ping.</summary>
    public event System.Action OnPong;

    public event System.Action<int> OnCalibrationRetrying;

    // ── Private state ─────────────────────────────────────────────────────────

    private OSCTransmitter _transmitter;
    private OSCBind _bindAck;
    private OSCBind _bindCalibResult;
    private OSCBind _bindFaceMetrics;
    private OSCBind _bindPong;

    private int _marginalRetryCount;
    private int _faceMetricsCount;

    // ── OSC address constants ─────────────────────────────────────────────────

    // Send
    private const string ADDR_SESSION_START  = "/session/start";
    private const string ADDR_SESSION_END    = "/experiment/session_end";
    private const string ADDR_CALIB_START    = "/calibration/start";
    private const string ADDR_CALIB_ABORT    = "/calibration/abort";
    private const string ADDR_TRIAL_START    = "/experiment/trial_start";
    private const string ADDR_TRIAL_END      = "/experiment/trial_end";
    private const string ADDR_PING           = "/ping";

    // Receive
    private const string ADDR_ACK            = "/experiment/ack";
    private const string ADDR_CALIB_RESULT   = "/calibration/result";
    private const string ADDR_FACE_METRICS   = "/face/metrics";
    private const string ADDR_PONG           = "/pong";

    // ── Public setters ────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the Python host address at any time, including after Start().
    /// Replaces the private-field reflection hack in SceneBootstrapper2.
    /// </summary>
    public void SetPythonHost(string host)
    {
        pythonHost = host;
        if (_transmitter != null)
            _transmitter.RemoteHost = host;
        FileLogger.Log("OSC", $"PythonHost updated → {host}");
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        SetupTransmitter();
        // Defer receiver setup by one frame so OscGazeInput.Awake() — which
        // creates the shared OSCReceiver on port 9000 — runs before we call
        // FindObjectOfType.  Without this, dynamic instantiation of OscGazeInput
        // at runtime could leave FindObjectOfType returning null, causing this
        // component to open a second socket on 9000 and throw a SocketException.
        StartCoroutine(SetupReceiverNextFrame());
    }

    private IEnumerator SetupReceiverNextFrame()
    {
        yield return null;
        SetupReceiver();
    }

    private void OnDestroy()
    {
        // Unbind only the binds we added; never destroy a receiver we didn't create.
        if (sharedReceiver != null)
        {
            if (_bindAck         != null) sharedReceiver.Unbind(_bindAck);
            if (_bindCalibResult != null) sharedReceiver.Unbind(_bindCalibResult);
            if (_bindFaceMetrics != null) sharedReceiver.Unbind(_bindFaceMetrics);
            if (_bindPong        != null) sharedReceiver.Unbind(_bindPong);
        }

        // Clear public events so any extOSC packet queued just before destroy
        // does not invoke subscribers that may already hold stale references.
        OnCalibrationResult   = null;
        OnFaceMetrics         = null;
        OnAck                 = null;
        OnPong                = null;
        OnCalibrationRetrying = null;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void SetupTransmitter()
    {
        // Always create a dedicated child object for the transmitter (send-only).
        var txObj = new GameObject("OSCTransmitter_Session");
        txObj.transform.SetParent(transform);

        _transmitter = txObj.AddComponent<OSCTransmitter>();
        _transmitter.RemoteHost = pythonHost;
        _transmitter.RemotePort = sendPort;
    }

    private void SetupReceiver()
    {
        // Try the Inspector-wired receiver first.
        if (sharedReceiver == null)
        {
            // Fallback: find the receiver OscGazeInput created at runtime.
            // OscGazeInput.Awake() runs before our deferred frame, so
            // FindObjectOfType should succeed; guard for the null case anyway.
            sharedReceiver = FindAnyObjectByType<OSCReceiver>();

            if (sharedReceiver == null)
            {
                // Last resort: create one. Wire SharedReceiver in the Inspector to avoid this.

                var rxObj = new GameObject("OSCReceiver_Session");
                rxObj.transform.SetParent(transform);
                sharedReceiver = rxObj.AddComponent<OSCReceiver>();
                sharedReceiver.LocalPort = 9000;
            }
        }

        // Bind all incoming addresses to this shared receiver.
        _bindAck         = sharedReceiver.Bind(ADDR_ACK,          OnAckReceived);
        _bindCalibResult = sharedReceiver.Bind(ADDR_CALIB_RESULT,  OnCalibResultReceived);
        _bindFaceMetrics = sharedReceiver.Bind(ADDR_FACE_METRICS,  OnFaceMetricsReceived);
        _bindPong        = sharedReceiver.Bind(ADDR_PONG,          OnPongReceived);

        FileLogger.Log("OSC", $"SEND: {pythonHost}:{sendPort} | RECV: port {sharedReceiver.LocalPort} | Binds: {ADDR_ACK}, {ADDR_CALIB_RESULT}, {ADDR_FACE_METRICS}, {ADDR_PONG}");
    }

    // ── Public API (called by ExperimentManager on the Expert side) ───────────

    /// <summary>
    /// Tell Python to start a session.
    /// Sends: /session/start [pid: string] [condition: string]
    /// </summary>
    public void StartSession(string pid, string condition)
    {
        if (_transmitter == null) { LogNotReady("StartSession"); return; }
        var msg = new OSCMessage(ADDR_SESSION_START,
            OSCValue.String(pid),
            OSCValue.String(condition));
        _transmitter.Send(msg);
        FileLogger.Log("OSC", $"SEND → {ADDR_SESSION_START}  pid={pid}  condition={condition}");
    }

    /// <summary>
    /// Tell Python to end the session.
    /// Sends: /experiment/session_end
    /// </summary>
    public void EndSession()
    {
        SendNoArgs(ADDR_SESSION_END);
    }

    /// <summary>
    /// Start a trial.
    /// Sends: /experiment/trial_start [trial_id: string]
    /// </summary>
    public void StartTrial(string trialId)
    {
        if (_transmitter == null) { LogNotReady("StartTrial"); return; }
        var msg = new OSCMessage(ADDR_TRIAL_START, OSCValue.String(trialId));
        _transmitter.Send(msg);
        FileLogger.Log("OSC", $"SEND → {ADDR_TRIAL_START}  trial_id={trialId}");
    }

    /// <summary>
    /// End the current trial.
    /// Sends: /experiment/trial_end
    /// </summary>
    public void EndTrial()
    {
        SendNoArgs(ADDR_TRIAL_END);
    }

    /// <summary>
    /// Start calibration (Webcam / WebcamFiltered conditions only).
    /// Sends: /calibration/start
    /// Resets the MARGINAL-retry counter.
    /// </summary>
    public void StartCalibration()
    {
        _marginalRetryCount = 0;
        SendNoArgs(ADDR_CALIB_START);
    }

    /// <summary>
    /// Abort an in-progress calibration.
    /// Sends: /calibration/abort
    /// </summary>
    public void AbortCalibration()
    {
        SendNoArgs(ADDR_CALIB_ABORT);
    }

    /// <summary>
    /// Send a /ping to check Python-side liveness.
    /// </summary>
    public void Ping()
    {
        SendNoArgs(ADDR_PING);
    }

    // ── Receive handlers ──────────────────────────────────────────────────────

    /// <summary>/experiment/ack [command: string] [status: string]</summary>
    private void OnAckReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 2) return;

        string command = message.Values[0].StringValue;
        string status  = message.Values[1].StringValue;

        FileLogger.Log("OSC", $"RECV ← {ADDR_ACK}  command={command}  status={status}");
        OnAck?.Invoke(command, status);
    }

    /// <summary>
    /// /calibration/result [quality: int] [err_x: float] [err_y: float]
    ///
    /// quality: 2 = PASS, 1 = MARGINAL, 0 = FAIL
    ///
    /// On MARGINAL: silently retry /calibration/start up to maxMarginalRetries times.
    /// OnCalibrationResult is only fired for the terminal result (PASS, FAIL, or
    /// MARGINAL after all retries are exhausted).
    /// </summary>
    private void OnCalibResultReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 3) return;

        int   quality = message.Values[0].IntValue;
        float errX    = message.Values[1].FloatValue;
        float errY    = message.Values[2].FloatValue;

        FileLogger.Log("OSC", $"RECV ← {ADDR_CALIB_RESULT}  quality={quality}  err=({errX:F3}, {errY:F3})");

        if (quality == 1 /* MARGINAL */ && _marginalRetryCount < maxMarginalRetries)
        {
            _marginalRetryCount++;
            FileLogger.Log("OSC", $"Calibration MARGINAL — auto-retry {_marginalRetryCount}/{maxMarginalRetries}.");
            OnCalibrationRetrying?.Invoke(_marginalRetryCount);
            SendNoArgs(ADDR_CALIB_START);
            return; // do NOT fire the event yet
        }

        // Terminal result: PASS, FAIL, or MARGINAL with retries exhausted.
        OnCalibrationResult?.Invoke(quality, errX, errY);
    }

    /// <summary>/face/metrics [iod_norm: float] [face_cx: float] [face_cy: float] [status: int]</summary>
    private void OnFaceMetricsReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 4) return;

        float iodNorm = message.Values[0].FloatValue;
        float faceCx  = message.Values[1].FloatValue;
        float faceCy  = message.Values[2].FloatValue;
        int   status  = message.Values[3].IntValue;

        _faceMetricsCount++;
        if (_faceMetricsCount % 30 == 0)
            FileLogger.Log("OSC", $"RECV ← {ADDR_FACE_METRICS}  status={status}  iodNorm={iodNorm:F3}  cx={faceCx:F3}  cy={faceCy:F3}");

        OnFaceMetrics?.Invoke(iodNorm, faceCx, faceCy, status);
    }

    /// <summary>/pong (response to /ping)</summary>
    private void OnPongReceived(OSCMessage message)
    {
        FileLogger.Log("OSC", $"RECV ← {ADDR_PONG}");
        OnPong?.Invoke();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SendNoArgs(string address)
    {
        if (_transmitter == null) { LogNotReady(address); return; }
        _transmitter.Send(new OSCMessage(address));
        FileLogger.Log("OSC", $"SEND → {address}");
    }

    private static void LogNotReady(string context)
    {
        Debug.LogWarning($"[OscSessionManager] Transmitter not ready — skipping {context}.");
    }
}
