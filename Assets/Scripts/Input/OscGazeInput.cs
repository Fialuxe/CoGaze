using UnityEngine;
using extOSC;

// Receives OSC /gaze messages (float x, float y, float blink) from the eye tracker and implements IGazeInput.
public class OscGazeInput : MonoBehaviour, IGazeInput
{
    [Header("OSC Settings")]
    [SerializeField] private int localPort = 9000;
    [SerializeField] private string gazeAddress = "/gaze";

    [Header("Smoothing")]
    // 指数移動平均の平滑化係数 (0=固定, 1=平滑化なし)。
    // α=0.5: 平均遅延 ~17ms（60Hz時）。5cmサークルが残留ジッターを視覚吸収するため重い平滑化は不要。
    // α=0.3 は過剰（遅延 ~39ms、VR で知覚可能）。α=0.5–0.6 を推奨。
    [SerializeField, Range(0f, 1f)] private float smoothingAlpha = 0.5f;

    [Header("Debug")]
    // ONにすると、OSCデータが届かない場合でも最後に受信した位置（未受信なら画面中央）に視線を表示し続ける。
    // 実験本番では必ずOFFにすること。
    [SerializeField] private bool debugKeepLastGaze;

    private OSCReceiver _oscReceiver;
    private OSCBind _gazeBind;
    private Vector3 _gazeData = new Vector3(0.5f, 0.5f, 0f);
    private Vector3 _smoothedGaze = new Vector3(0.5f, 0.5f, 0f);
    private bool _isAvailable;
    private int _receiveCount;

    // 視線断検出: 最終受信からこの秒数を超えて無音なら「視線喪失」とみなす。
    // Tobii 等は通常 60Hz 以上で送信するため 0.5s は誤検出せず、かつ断を素早く検出できる。
    private const float k_gazeTimeoutSec = 0.5f;
    private float _lastDataTime = -1f;
    private bool _lossLogged;
    private int _warnCounter;

    // blink は平滑化しない（0/1の二値信号）。x,y のみ EMA で平滑化。
    public Vector3 GazeData => new Vector3(_smoothedGaze.x, _smoothedGaze.y, _gazeData.z);
    // debugKeepLastGaze=true のとき、タイムアウトを無視して常に有効を返す（デバッグ専用）。
    public bool IsAvailable => debugKeepLastGaze
        ? true
        : _isAvailable && (Time.realtimeSinceStartup - _lastDataTime) < k_gazeTimeoutSec;

    private void Awake()
    {
        SetupOscReceiver();
    }

    private void OnDestroy()
    {
        if (_oscReceiver != null && _gazeBind != null)
            _oscReceiver.Unbind(_gazeBind);
    }

    private void Update()
    {
        _warnCounter++;
        if (!_isAvailable && _warnCounter % 300 == 0)
            FileLogger.Log("WARN", $"[OscGazeInput] Python gaze stream not started on port {localPort}. Head-centre fallback active on Worker.");
        // 受信していたが無音になった瞬間を検出し、一度だけ通知する。
        // 併せて blink=1 を立て、古い視線を「有効な注視」として下流に流さない。
        // debugKeepLastGaze=true のときは視線を消さずに最終位置を保持する。
        if (!debugKeepLastGaze && _isAvailable && !IsAvailable && !_lossLogged)
        {
            _lossLogged = true;
            _gazeData.z = 1f;
            FileLogger.Log("OSC", $"OscGazeInput gaze stream lost (no message for {k_gazeTimeoutSec:F1}s). Reporting no-gaze (blink=1).");
        }
        if (_lossLogged && _warnCounter % 300 == 0 && !IsAvailable)
            FileLogger.Log("WARN", $"[OscGazeInput] Gaze stream still unavailable (port {localPort}). Head-centre fallback remains active.");
    }

    private void SetupOscReceiver()
    {
        // Reuse an OSCReceiver on the same port if one already exists (OscSessionManager
        // creates one during startup; creating a second on the same port throws a SocketException).
        var existing = FindAnyObjectByType<OSCReceiver>();
        if (existing != null && existing.LocalPort == localPort)
        {
            _oscReceiver = existing;
        }
        else
        {
            GameObject receiverObj = new GameObject("OSCReceiver_Gaze");
            receiverObj.transform.SetParent(transform);
            _oscReceiver = receiverObj.AddComponent<OSCReceiver>();
            _oscReceiver.LocalPort = localPort;
        }

        _gazeBind = _oscReceiver.Bind(gazeAddress, OnGazeMessageReceived);
        FileLogger.Log("OSC", $"OscGazeInput bound /gaze on port {localPort} (receiver: {_oscReceiver.gameObject.name})");
    }

    private void OnGazeMessageReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 2) return;

        float x = message.Values[0].FloatValue;
        float y = message.Values[1].FloatValue;
        // Python format: x y mesh_certainty eye_certainty source condition
        // High certainty = eye is tracked and open. Invert: low certainty → treat as blink (hide gaze).
        float meshCertainty = message.Values.Count >= 3 ? message.Values[2].FloatValue : 1f;
        float eyeCertainty  = message.Values.Count >= 4 ? message.Values[3].FloatValue : 1f;
        float blink = (meshCertainty < 0.5f || eyeCertainty < 0.5f) ? 1f : 0f;

        // Tobiiなどのアイトラッカーは画面左上を(0,0)、右下を(1,1)とする。
        // UnityのViewportは左下を(0,0)、右上を(1,1)とするため、Y座標を反転させる。
        float rawX = Mathf.Clamp01(x);
        float rawY = 1.0f - Mathf.Clamp01(y);

        // EMA平滑化: _smoothedGaze = α * raw + (1-α) * _smoothedGaze
        // 初回受信時はスムーズ値を生データで初期化してスパイクを防ぐ。
        if (!_isAvailable)
            _smoothedGaze = new Vector3(rawX, rawY, 0f);
        else
        {
            _smoothedGaze.x = smoothingAlpha * rawX + (1f - smoothingAlpha) * _smoothedGaze.x;
            _smoothedGaze.y = smoothingAlpha * rawY + (1f - smoothingAlpha) * _smoothedGaze.y;
        }

        _gazeData = new Vector3(rawX, rawY, blink > 0.5f ? 1f : 0f);
        _isAvailable = true;
        // extOSC は受信パケットを OSCReceiver.Update()（メインスレッド）でディスパッチするため
        // ここで Time.realtimeSinceStartup を読むのは安全。
        _lastDataTime = Time.realtimeSinceStartup;
        _lossLogged = false;
        _receiveCount++;
        if (_receiveCount == 1)
            FileLogger.Log("OSC", $"OscGazeInput first message received. x={_gazeData.x:F3} y={_gazeData.y:F3} blink={_gazeData.z} mesh_certainty={meshCertainty:F2} eye_certainty={eyeCertainty:F2}");
        else if (_receiveCount % 300 == 0)
            FileLogger.Log("OSC", $"OscGazeInput received {_receiveCount} messages. x={_gazeData.x:F3} y={_gazeData.y:F3} blink={_gazeData.z}");
    }
}
