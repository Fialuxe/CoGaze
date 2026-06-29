using System.Collections;
using UnityEngine;
using extOSC;

// OSC bridge Unity→Python (send 9001) and Python→Unity (recv 9000, shared with OscGazeInput).
// Port 9000 has one socket — use SharedReceiver in Inspector or defer-frame fallback.
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
    [SerializeField] private int maxMarginalRetries;

    [Header("Python Auto-Launch")]
    [Tooltip("Pythonインタープリタへのパス（例: python / python3 / C:\\Python312\\python.exe）")]
    [SerializeField] private string autoLaunchPythonExe = "python";
    [Tooltip("起動するPythonスクリプトのフルパス。空のままにすると自動起動を無効化。")]
    [SerializeField] private string autoLaunchScriptPath = "";

    // ── Public events ─────────────────────────────────────────────────────────

    public event System.Action<int, float, float>        OnCalibrationResult;  // quality, err_x, err_y
    public event System.Action<float, float, float, int> OnFaceMetrics;        // iod_norm, cx, cy, status
    public event System.Action<string, string>           OnAck;                // command, status
    public event System.Action                           OnPong;

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
    private const string k_addrSessionStart   = "/session/start";
    private const string k_addrSessionEnd     = "/experiment/session_end";
    private const string k_addrCalibStart     = "/calibration/start";
    private const string k_addrCalibAbort     = "/calibration/abort";
    private const string k_addrCalibReset     = "/calibration/reset";
    private const string k_addrCalibSample    = "/calibration/sample";
    private const string k_addrCalibCompute   = "/calibration/compute";
    private const string k_addrTrialStart     = "/experiment/trial_start";
    private const string k_addrTrialEnd       = "/experiment/trial_end";
    private const string k_addrPing            = "/ping";

    // Receive
    private const string k_addrAck            = "/experiment/ack";
    private const string k_addrCalibResult   = "/calibration/result";
    private const string k_addrFaceMetrics   = "/face/metrics";
    private const string k_addrPong           = "/pong";

    // ── Public setters ────────────────────────────────────────────────────────

    public void SetPythonHost(string host)
    {
        pythonHost = host;
        if (_transmitter != null)
            _transmitter.RemoteHost = host;
        FileLogger.Log("OSC", $"PythonHost updated → {host}");
    }

    public bool TryAutoLaunchPython()
    {
        if (string.IsNullOrEmpty(autoLaunchScriptPath))
        {
            FileLogger.Log("OSC", "TryAutoLaunchPython: autoLaunchScriptPath is empty — skipped.");
            return false;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName         = autoLaunchPythonExe,
                Arguments        = autoLaunchScriptPath,
                UseShellExecute  = true,
                CreateNoWindow   = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(autoLaunchScriptPath) ?? "",
            };
            System.Diagnostics.Process.Start(psi);
            FileLogger.Log("OSC", $"TryAutoLaunchPython: launched '{autoLaunchPythonExe} {autoLaunchScriptPath}'");
            return true;
        }
        catch (System.Exception ex)
        {
            FileLogger.Log("OSC", $"TryAutoLaunchPython: FAILED — {ex.Message}");
            UnityEngine.Debug.LogWarning($"[OscSessionManager] Python auto-launch failed: {ex.Message}");
            return false;
        }
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
        _bindAck         = sharedReceiver.Bind(k_addrAck,          OnAckReceived);
        _bindCalibResult = sharedReceiver.Bind(k_addrCalibResult,  OnCalibResultReceived);
        _bindFaceMetrics = sharedReceiver.Bind(k_addrFaceMetrics,  OnFaceMetricsReceived);
        _bindPong        = sharedReceiver.Bind(k_addrPong,          OnPongReceived);

        FileLogger.Log("OSC", $"SEND: {pythonHost}:{sendPort} | RECV: port {sharedReceiver.LocalPort} | Binds: {k_addrAck}, {k_addrCalibResult}, {k_addrFaceMetrics}, {k_addrPong}");
    }

    // ── Public API (called by ExperimentManager2 on the Expert side) ───────────

    public void StartSession(string pid, string condition)
    {
        if (_transmitter == null) { LogNotReady("StartSession"); return; }
        var msg = new OSCMessage(k_addrSessionStart,
            OSCValue.String(pid),
            OSCValue.String(condition));
        _transmitter.Send(msg);
        FileLogger.Log("OSC", $"SEND → {k_addrSessionStart}  pid={pid}  condition={condition}");
    }

    public void EndSession()
    {
        SendNoArgs(k_addrSessionEnd);
    }

    public void StartTrial(string trialId)
    {
        if (_transmitter == null) { LogNotReady("StartTrial"); return; }
        var msg = new OSCMessage(k_addrTrialStart, OSCValue.String(trialId));
        _transmitter.Send(msg);
        FileLogger.Log("OSC", $"SEND → {k_addrTrialStart}  trial_id={trialId}");
    }

    public void EndTrial()
    {
        SendNoArgs(k_addrTrialEnd);
    }

    public void StartCalibration()
    {
        _marginalRetryCount = 0;
        SendNoArgs(k_addrCalibStart);
    }

    public void AbortCalibration()
    {
        SendNoArgs(k_addrCalibAbort);
    }

    public void SendCalibrationReset()
    {
        SendNoArgs(k_addrCalibReset);
    }

    public void SendCalibrationSample(float x, float y)
    {
        if (_transmitter == null) { LogNotReady("SendCalibrationSample"); return; }
        var msg = new OSCMessage(k_addrCalibSample, OSCValue.Float(x), OSCValue.Float(y));
        _transmitter.Send(msg);
    }

    public void SendCalibrationCompute()
    {
        SendNoArgs(k_addrCalibCompute);
    }

    public void Ping()
    {
        SendNoArgs(k_addrPing);
    }

    // ── Receive handlers ──────────────────────────────────────────────────────

    private void OnAckReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 2) return;

        string command = message.Values[0].StringValue;
        string status  = message.Values[1].StringValue;

        FileLogger.Log("OSC", $"RECV ← {k_addrAck}  command={command}  status={status}");
        OnAck?.Invoke(command, status);
    }

    private void OnCalibResultReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 3) return;

        int   quality = message.Values[0].IntValue;
        float errX    = message.Values[1].FloatValue;
        float errY    = message.Values[2].FloatValue;

        FileLogger.Log("OSC", $"RECV ← {k_addrCalibResult}  quality={quality}  err=({errX:F3}, {errY:F3})");

        if (quality == 1 /* MARGINAL */ && _marginalRetryCount < maxMarginalRetries)
        {
            _marginalRetryCount++;
            FileLogger.Log("OSC", $"Calibration MARGINAL — auto-retry {_marginalRetryCount}/{maxMarginalRetries}.");
            // Unity-driven flow: fire the retrying event so ExperimentManager2 can restart
            // WebcamCalibrationUI. Do NOT send /calibration/start — Python no longer handles it.
            OnCalibrationRetrying?.Invoke(_marginalRetryCount);
            return; // do NOT fire the terminal event yet
        }

        // Terminal result: PASS, FAIL, or MARGINAL with retries exhausted.
        OnCalibrationResult?.Invoke(quality, errX, errY);
    }

    private void OnFaceMetricsReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 4) return;

        float iodNorm = message.Values[0].FloatValue;
        float faceCx  = message.Values[1].FloatValue;
        float faceCy  = message.Values[2].FloatValue;
        int   status  = message.Values[3].IntValue;

        _faceMetricsCount++;
        if (_faceMetricsCount % 30 == 0)
            FileLogger.Log("OSC", $"RECV ← {k_addrFaceMetrics}  status={status}  iodNorm={iodNorm:F3}  cx={faceCx:F3}  cy={faceCy:F3}");

        OnFaceMetrics?.Invoke(iodNorm, faceCx, faceCy, status);
    }

    private void OnPongReceived(OSCMessage message)
    {
        FileLogger.Log("OSC", $"RECV ← {k_addrPong}");
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
