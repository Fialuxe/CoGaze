using System;
using UnityEngine;

// IMGUI startup config panel for Expert (PC); no EventSystem required.
public class StartupUI : MonoBehaviour
{
    public event Action OnConfirmed;

    private StartupConfig _config;

    private string   _participantId;
    private int      _orderIndex;
    private string   _pythonHost;
    private string[] _micDevices;
    private int      _micIndex;
    private bool     _offlineMode;

    // ── Live mic test (lets the operator confirm Unity actually captures audio
    //    from the selected device BEFORE entering the experiment) ──
    private AudioClip _testClip;
    private string    _testDevice;
    private int       _testReadPos;
    private float     _testLevel;   // smoothed 0..1 for the bar
    private float     _testPeak;    // raw peak this frame (0 = no signal)
    private const int k_testSr = 16000;

    private GUIStyle _panelStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _hintStyle;
    private GUIStyle _inputStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _toggleStyle;
    private GUIStyle _micButtonStyle;
    private GUIStyle _issueStyle;
    private Texture2D _barBgTex;
    private Texture2D _barFillTex;
    private bool     _stylesBuilt;

    // ── Startup self-check (cached; recomputed only when id/order/mic changes) ──
    private System.Collections.Generic.List<StartupSelfCheck.Issue> _issues;
    private string _checkedId;
    private int    _checkedOrder = int.MinValue;
    private string _checkedMic;

    // ── Python process detection ──────────────────────────────────────────────
    private enum PythonStatus { Unknown, Checking, Running, NotRunning, Launching }

    private string            _pythonScriptDir;
    private PythonStatus      _pythonStatus  = PythonStatus.Unknown;
    private OscSessionManager _oscSession;
    private Coroutine         _pingCo;
    private bool              _pongReceived;

    private const float k_panelW = 480f;
    private float       _panelH;

    public void Initialize(StartupConfig config)
    {
        _config          = config;
        _participantId   = config.participantId;
        _orderIndex      = Mathf.Clamp(config.participantOrderIndex, 0, 23);
        _pythonHost      = config.pythonHost;
        _offlineMode     = config.offlineMode;
        _pythonScriptDir = config.pythonScriptDir;

        _micDevices = Microphone.devices.Length > 0
            ? Microphone.devices
            : new[] { "(no microphone found)" };

        // Find saved device index; fall back to 0
        _micIndex = Mathf.Max(0, Array.IndexOf(_micDevices, config.microphoneDevice));

        // Panel height: base layout + 28px per mic device + 40px offline toggle + ~70px mic-test meter
        // + ~230px for the startup self-check section (header + up to ~7 rows + condition preview).
        // + ~120px for Python script dir field + status/launch row.
        _panelH = 310f + _micDevices.Length * 28f + 40f + 70f + 230f + 120f;
    }

    private void Start()
    {
        _oscSession = FindAnyObjectByType<OscSessionManager>();
        _pingCo     = StartCoroutine(PingPython(2f));
    }

    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _panelStyle = new GUIStyle(GUI.skin.box);
        _panelStyle.normal.background = MakeTex(1, 1, new Color(0.15f, 0.15f, 0.15f, 0.97f));

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _titleStyle.normal.textColor = Color.white;

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 15,
            alignment = TextAnchor.MiddleLeft,
        };
        _labelStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

        _hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize   = 12,
            fontStyle  = FontStyle.Italic,
            alignment  = TextAnchor.MiddleLeft,
            wordWrap   = true,
        };
        _hintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f);

        _inputStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize  = 16,
            alignment = TextAnchor.MiddleLeft,
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 18,
            fontStyle = FontStyle.Bold,
        };
        _buttonStyle.normal.textColor  = Color.white;
        _buttonStyle.hover.textColor   = Color.white;
        _buttonStyle.active.textColor  = Color.white;
        _buttonStyle.normal.background = MakeTex(1, 1, new Color(0.2f, 0.55f, 1f, 1f));
        _buttonStyle.hover.background  = MakeTex(1, 1, new Color(0.3f, 0.65f, 1f, 1f));
        _buttonStyle.active.background = MakeTex(1, 1, new Color(0.1f, 0.45f, 0.9f, 1f));

        _toggleStyle = new GUIStyle(GUI.skin.toggle)
        {
            fontSize = 15,
        };
        _toggleStyle.normal.textColor = new Color(1f, 0.8f, 0.3f);

        _micButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, alignment = TextAnchor.MiddleLeft };

        _issueStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, alignment = TextAnchor.UpperLeft };
        _issueStyle.normal.textColor = Color.white;   // tinted per-severity via GUI.color

        _barBgTex   = MakeTex(1, 1, new Color(0.25f, 0.25f, 0.25f, 1f));
        _barFillTex = MakeTex(1, 1, new Color(0.20f, 0.90f, 0.40f, 1f));
    }

    private void OnGUI()
    {
        BuildStyles();

        var oldColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = oldColor;

        float px = (Screen.width  - k_panelW) * 0.5f;
        float py = (Screen.height - _panelH) * 0.5f;

        GUI.Box(new Rect(px, py, k_panelW, _panelH), GUIContent.none, _panelStyle);

        float x = px + 24f;
        float w = k_panelW - 48f;
        float y = py + 16f;

        // ── Title ─────────────────────────────────────────────
        GUI.Label(new Rect(px, y, k_panelW, 32f), CoGazeStrings.Startup_Title, _titleStyle);
        y += 44f;

        // ── Participant ID ────────────────────────────────────
        GUI.Label(new Rect(x, y, w, 22f), CoGazeStrings.Startup_LabelParticipantId, _labelStyle);
        y += 24f;
        _participantId = GUI.TextField(new Rect(x, y, w, 32f), _participantId, _inputStyle);
        y += 40f;

        // ── Condition order ───────────────────────────────────
        GUI.Label(new Rect(x, y, w, 22f), $"参加者インデックス (0-23):  {_orderIndex}", _labelStyle);
        y += 24f;
        _orderIndex = Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(x, y + 6f, w - 60f, 18f), _orderIndex, 0f, 23f));
        GUI.Label(new Rect(x + w - 52f, y, 52f, 30f), $"  {_orderIndex} / 23", _labelStyle);
        y += 40f;

        // ── Python host ───────────────────────────────────────
        GUI.Label(new Rect(x, y, w, 22f), CoGazeStrings.Startup_LabelPythonHost, _labelStyle);
        y += 24f;
        _pythonHost = GUI.TextField(new Rect(x, y, w, 32f), _pythonHost, _inputStyle);
        GUI.Label(new Rect(x, y + 34f, w, 18f), CoGazeStrings.Startup_HintPythonHost, _hintStyle);
        y += 62f;

        // ── Python script dir ─────────────────────────────────
        GUI.Label(new Rect(x, y, w, 22f), "Python スクリプトディレクトリ:", _labelStyle);
        y += 24f;
        _pythonScriptDir = GUI.TextField(new Rect(x, y, w, 32f), _pythonScriptDir, _inputStyle);
        GUI.Label(new Rect(x, y + 34f, w, 18f), "例: C:\\GitHub\\WebcamEyeTracking  (自動起動に必要)", _hintStyle);
        y += 60f;

        // ── Microphone ────────────────────────────────────────
        GUI.Label(new Rect(x, y, w, 22f), CoGazeStrings.Startup_LabelMicrophone, _labelStyle);
        y += 24f;
        for (int i = 0; i < _micDevices.Length; i++)
        {
            bool selected = i == _micIndex;
            GUI.backgroundColor = selected ? new Color(0.2f, 0.6f, 1f) : new Color(0.3f, 0.3f, 0.3f);
            if (GUI.Button(new Rect(x, y, w, 26f), _micDevices[i], _micButtonStyle))
                _micIndex = i;
            y += 28f;
        }
        GUI.backgroundColor = Color.white;
        y += 6f;

        // ── Live mic level meter (test) ───────────────────────
        GUI.Label(new Rect(x, y, w, 20f), "マイクテスト（喋ってバーが動けばOK）:", _labelStyle);
        y += 22f;
        GUI.DrawTexture(new Rect(x, y, w, 16f), _barBgTex);
        float fill = Mathf.Clamp01(_testLevel);
        if (fill > 0.02f) GUI.DrawTexture(new Rect(x, y, w * fill, 16f), _barFillTex);
        y += 20f;
        bool live = _testClip != null && Microphone.IsRecording(_testDevice);
        string status = !live          ? "× キャプチャ開始失敗（デバイス/権限）"
                      : _testPeak <= 0.0002f ? "× 信号ゼロ — Windowsのマイク権限OFF か ミュートの可能性"
                      : "○ 入力検出中（このデバイスでOK）";
        GUI.Label(new Rect(x, y, w, 18f), status, _hintStyle);
        y += 24f;

        // ── Offline mode ──────────────────────────────────────
        _offlineMode = GUI.Toggle(new Rect(x, y, w, 28f),
            _offlineMode, CoGazeStrings.Startup_ToggleOfflineMode, _toggleStyle);
        y += 36f;

        // ── Checklist ─────────────────────────────────────────
        RefreshIssues();
        GUI.Label(new Rect(x, y, w, 20f), "チェックリスト:", _labelStyle);
        y += 22f;

        // Python status row (with launch/re-ping button)
        {
            string pyLabel =
                _pythonStatus == PythonStatus.Checking  ? "確認中..." :
                _pythonStatus == PythonStatus.Running   ? "起動済み ✓" :
                _pythonStatus == PythonStatus.NotRunning? "未起動" :
                _pythonStatus == PythonStatus.Launching ? "起動中..." : "不明";
            Color pyColor =
                _pythonStatus == PythonStatus.Running    ? new Color(0.4f, 1f, 0.4f) :
                _pythonStatus == PythonStatus.NotRunning ? new Color(1f, 0.5f, 0.4f) :
                                                           new Color(0.9f, 0.85f, 0.4f);
            string pyPrefix =
                _pythonStatus == PythonStatus.Running ? "   " :
                _pythonStatus == PythonStatus.NotRunning ? "● " : "▲ ";
            var prevCol = GUI.color; GUI.color = pyColor;
            GUI.Label(new Rect(x, y, w * 0.58f, 20f), pyPrefix + "Python 視線追跡: " + pyLabel, _issueStyle);
            GUI.color = prevCol;

            float bx = x + w * 0.62f, bw = w * 0.38f;
            if (_pythonStatus == PythonStatus.Running)
            {
                if (GUI.Button(new Rect(bx, y - 1f, bw, 22f), "再確認", _micButtonStyle))
                {
                    if (_pingCo != null) StopCoroutine(_pingCo);
                    _pingCo = StartCoroutine(PingPython(2f));
                }
            }
            else if (_pythonStatus != PythonStatus.Checking && _pythonStatus != PythonStatus.Launching)
            {
                bool canLaunch = !string.IsNullOrWhiteSpace(_pythonScriptDir);
                GUI.enabled = canLaunch;
                if (GUI.Button(new Rect(bx, y - 1f, bw, 22f), "Python を起動", _micButtonStyle))
                {
                    if (_pingCo != null) StopCoroutine(_pingCo);
                    _pingCo = StartCoroutine(LaunchAndRePing());
                }
                GUI.enabled = true;
            }
            y += 22f;
        }

        // Remaining self-check issues
        foreach (var iss in _issues)
        {
            Color c = iss.Severity == StartupSelfCheck.Severity.Fatal   ? new Color(1f, 0.45f, 0.40f)
                    : iss.Severity == StartupSelfCheck.Severity.Warning ? new Color(1f, 0.80f, 0.30f)
                                                                        : new Color(0.6f, 0.85f, 0.6f);
            string prefix = iss.Severity == StartupSelfCheck.Severity.Fatal   ? "● "
                          : iss.Severity == StartupSelfCheck.Severity.Warning ? "▲ "
                                                                              : "   ";
            float rowH = iss.Message.StartsWith("条件順") ? 38f : 20f;   // preview wraps to ~2 lines
            var prev = GUI.color; GUI.color = c;
            GUI.Label(new Rect(x, y, w, rowH), prefix + iss.Message, _issueStyle);
            GUI.color = prev;
            y += rowH;
        }
        y += 8f;

        // ── Start button (blocked while any Fatal check fails) ─
        bool hasFatal = StartupSelfCheck.HasFatal(_issues);
        GUI.enabled = !hasFatal;
        if (GUI.Button(new Rect(x + w * 0.5f - 90f, y, 180f, 40f),
                hasFatal ? "起動前チェックを修正してください" : CoGazeStrings.Startup_ButtonStart, _buttonStyle))
            Confirm();
        GUI.enabled = true;
    }

    // Recompute the self-check only when the participant id / order index / mic selection changes
    // (OnGUI runs every frame; the checks do file I/O and native device enumeration, so caching
    // avoids per-frame hits).
    private void RefreshIssues()
    {
        string mic = _micDevices[Mathf.Clamp(_micIndex, 0, _micDevices.Length - 1)];
        if (_issues != null && _participantId == _checkedId && _orderIndex == _checkedOrder && mic == _checkedMic) return;
        _checkedId    = _participantId;
        _checkedOrder = _orderIndex;
        _checkedMic   = mic;
        _issues = StartupSelfCheck.Run(_participantId, _orderIndex, includeInstructions: true, micDevice: mic);
    }

    private void Confirm()
    {
        _config.participantId         = _participantId.Trim();
        _config.participantOrderIndex = _orderIndex;
        _config.pythonHost            = string.IsNullOrWhiteSpace(_pythonHost) ? "127.0.0.1" : _pythonHost.Trim();
        _config.microphoneDevice      = _micDevices[_micIndex];
        _config.offlineMode           = _offlineMode;
        _config.pythonScriptDir       = _pythonScriptDir?.Trim() ?? "";
        _config.Save();
        OnConfirmed?.Invoke();
        Destroy(this);
    }

    // ── Live mic test ─────────────────────────────────────────
    // Captures from the currently-selected device into a 1s loop clip and
    // computes a level so the operator can confirm Unity sees real audio.
    // Released before Confirm() so PV2's Recorder can take the device cleanly.
    private void Update()
    {
        if (_micDevices == null || _micDevices.Length == 0) return;
        string dev = _micDevices[Mathf.Clamp(_micIndex, 0, _micDevices.Length - 1)];
        if (dev == "(no microphone found)") { _testLevel = 0f; return; }

        if (dev != _testDevice)   // selection changed → restart capture
        {
            StopTestMic();
            _testDevice = dev;
            try { _testClip = Microphone.Start(dev, true, 1, k_testSr); _testReadPos = 0; }
            catch { _testClip = null; }
        }
        if (_testClip == null) return;

        int len = _testClip.samples;
        if (len <= 0) return;
        int pos   = Microphone.GetPosition(_testDevice);
        int avail = (pos - _testReadPos + len) % len;
        if (avail <= 0) { _testLevel = Mathf.Lerp(_testLevel, 0f, Time.deltaTime * 8f); return; }
        avail = Mathf.Min(avail, k_testSr / 10);   // up to 100 ms per frame

        var buf = new float[avail];
        _testClip.GetData(buf, _testReadPos);
        _testReadPos = (_testReadPos + avail) % len;

        float sum = 0f, peak = 0f;
        for (int i = 0; i < avail; i++) { float a = buf[i] < 0 ? -buf[i] : buf[i]; sum += buf[i] * buf[i]; if (a > peak) peak = a; }
        _testPeak = peak;
        float target = Mathf.Clamp01(Mathf.Sqrt(sum / avail) * 6f);   // scale RMS for visibility
        _testLevel = Mathf.Max(target, Mathf.Lerp(_testLevel, target, Time.deltaTime * 10f));
    }

    private void StopTestMic()
    {
        if (!string.IsNullOrEmpty(_testDevice))
        {
            try { if (Microphone.IsRecording(_testDevice)) Microphone.End(_testDevice); } catch { }
        }
        _testClip = null;
    }

    private void OnDestroy()
    {
        StopTestMic();
        if (_pingCo != null) StopCoroutine(_pingCo);
        if (_oscSession != null) _oscSession.OnPong -= HandlePong;
    }

    // ── Python detection / launch ─────────────────────────────────────────────

    private void HandlePong() => _pongReceived = true;

    private System.Collections.IEnumerator PingPython(float timeout)
    {
        _pythonStatus = PythonStatus.Checking;
        // Wait for OscSessionManager.SetupReceiverNextFrame() to complete
        yield return null;
        yield return null;

        if (_oscSession == null)
            _oscSession = FindAnyObjectByType<OscSessionManager>();
        if (_oscSession == null) { _pythonStatus = PythonStatus.NotRunning; yield break; }

        _pongReceived = false;
        _oscSession.OnPong += HandlePong;
        _oscSession.Ping();

        float elapsed = 0f;
        while (elapsed < timeout && !_pongReceived)
        {
            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }

        _oscSession.OnPong -= HandlePong;
        _pythonStatus = _pongReceived ? PythonStatus.Running : PythonStatus.NotRunning;
        _pingCo = null;
    }

    private System.Collections.IEnumerator LaunchAndRePing()
    {
        _pythonStatus = PythonStatus.Launching;
        LaunchPython();
        // Give Python time to initialize OSC listener
        yield return new WaitForSecondsRealtime(4f);
        yield return StartCoroutine(PingPython(3f));
    }

    private void LaunchPython()
    {
        string baseDir = _pythonScriptDir?.Trim() ?? "";
        if (string.IsNullOrEmpty(baseDir)) { _pythonStatus = PythonStatus.NotRunning; return; }
        string srcDir = System.IO.Path.Combine(baseDir, "src");

        // pythonw.exe is the windowless Python launcher bundled with every CPython install.
        // Falls back to cmd start if pythonw is not found.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName         = "pythonw",
            Arguments        = "-m main",
            WorkingDirectory = srcDir,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };
        try
        {
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // pythonw not found — fall back to detached cmd (window visible)
            psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName       = "cmd.exe",
                Arguments      = $"/c start \"\" /d \"{srcDir}\" cmd.exe /k python -m main",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            try   { System.Diagnostics.Process.Start(psi); }
            catch (Exception ex2)
            {
                Debug.LogWarning($"[StartupUI] Python launch failed: {ex2.Message}");
                _pythonStatus = PythonStatus.NotRunning;
            }
        }
    }

    private static Texture2D MakeTex(int w, int h, Color col)
    {
        var tex = new Texture2D(w, h);
        tex.SetPixel(0, 0, col);
        tex.Apply();
        return tex;
    }
}
