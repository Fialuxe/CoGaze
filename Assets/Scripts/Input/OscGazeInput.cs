using UnityEngine;
using extOSC;

/// <summary>
/// extOSCのOSCReceiverを使って外部アイトラッカーからOSCメッセージを受信し、
/// (x, y, blink) 形式のデータをIGazeInputとして返す。
/// OSCアドレス: /gaze  フォーマット: float x, float y, float blink
/// </summary>
public class OscGazeInput : MonoBehaviour, IGazeInput
{
    [Header("OSC Settings")]
    [SerializeField] private int localPort = 9000;
    [SerializeField] private string gazeAddress = "/gaze";

    private OSCReceiver oscReceiver;
    private OSCBind gazeBind;
    private Vector3 gazeData = new Vector3(0.5f, 0.5f, 0f);
    private bool isAvailable = false;
    private int _receiveCount;

    // 視線断検出: 最終受信からこの秒数を超えて無音なら「視線喪失」とみなす。
    // Tobii 等は通常 60Hz 以上で送信するため 0.5s は誤検出せず、かつ断を素早く検出できる。
    private const float GAZE_TIMEOUT_SEC = 0.5f;
    private float _lastDataTime = -1f;
    private bool  _lossLogged = false;

    public Vector3 GazeData => gazeData;
    // 最終受信からの経過時間で可用性を判定する（古いデータを「有効」と報告し続けない）。
    public bool IsAvailable => isAvailable
        && (Time.realtimeSinceStartup - _lastDataTime) < GAZE_TIMEOUT_SEC;

    private void Awake()
    {
        SetupOscReceiver();
    }

    private void SetupOscReceiver()
    {
        // Reuse an OSCReceiver on the same port if one already exists (OscSessionManager
        // creates one during startup; creating a second on the same port throws a SocketException).
        var existing = FindAnyObjectByType<OSCReceiver>();
        if (existing != null && existing.LocalPort == localPort)
        {
            oscReceiver = existing;
        }
        else
        {
            GameObject receiverObj = new GameObject("OSCReceiver_Gaze");
            receiverObj.transform.SetParent(transform);
            oscReceiver = receiverObj.AddComponent<OSCReceiver>();
            oscReceiver.LocalPort = localPort;
        }

        // /gaze アドレスをバインド
        gazeBind = oscReceiver.Bind(gazeAddress, OnGazeMessageReceived);

        FileLogger.Log("OSC", $"OscGazeInput bound /gaze on port {localPort} (receiver: {oscReceiver.gameObject.name})");
    }

    private void OnGazeMessageReceived(OSCMessage message)
    {
        if (message.Values == null || message.Values.Count < 3) return;

        float x = message.Values[0].FloatValue;
        float y = message.Values[1].FloatValue;
        float blink = message.Values[2].FloatValue;

        // Tobiiなどのアイトラッカーは画面左上を(0,0)、右下を(1,1)とする。
        // UnityのViewportは左下を(0,0)、右上を(1,1)とするため、Y座標を反転させる。
        float unityY = 1.0f - Mathf.Clamp01(y);

        gazeData = new Vector3(
            Mathf.Clamp01(x),
            unityY,
            blink > 0.5f ? 1f : 0f
        );
        isAvailable = true;
        // extOSC は受信パケットを OSCReceiver.Update()（メインスレッド）でディスパッチするため
        // ここで Time.realtimeSinceStartup を読むのは安全。
        _lastDataTime = Time.realtimeSinceStartup;
        _lossLogged = false;
        _receiveCount++;
        if (_receiveCount == 1)
            FileLogger.Log("OSC", $"OscGazeInput first message received. x={gazeData.x:F3} y={gazeData.y:F3} blink={gazeData.z}");
        else if (_receiveCount % 300 == 0)
            FileLogger.Log("OSC", $"OscGazeInput received {_receiveCount} messages. x={gazeData.x:F3} y={gazeData.y:F3} blink={gazeData.z}");
    }

    private void Update()
    {
        // 受信していたが無音になった瞬間を検出し、一度だけ通知する。
        // 併せて blink=1 を立て、古い視線を「有効な注視」として下流に流さない。
        if (isAvailable && !IsAvailable && !_lossLogged)
        {
            _lossLogged = true;
            gazeData.z = 1f;
            FileLogger.Log("OSC", $"OscGazeInput gaze stream lost (no message for {GAZE_TIMEOUT_SEC:F1}s). Reporting no-gaze (blink=1).");
        }
    }

    private void OnDestroy()
    {
       if (oscReceiver != null && gazeBind != null)
        {
            oscReceiver.Unbind(gazeBind);
        }
    }
}
