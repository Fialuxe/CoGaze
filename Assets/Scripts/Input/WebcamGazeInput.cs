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
    [SerializeField] private int localPort = 8000; // Tobii OSC Serverのデフォルト(8000)に合わせる
    [SerializeField] private string gazeAddress = "/gaze";

    private OSCReceiver oscReceiver;
    private OSCBind gazeBind;
    private Vector3 gazeData = new Vector3(0.5f, 0.5f, 0f);
    private bool isAvailable = false;

    public Vector3 GazeData => gazeData;
    public bool IsAvailable => isAvailable;

    private void Start()
    {
        SetupOscReceiver();
    }

    private void SetupOscReceiver()
    {
        // OSCReceiverを子オブジェクトに生成
        GameObject receiverObj = new GameObject("OSCReceiver_Gaze");
        receiverObj.transform.SetParent(transform);

        oscReceiver = receiverObj.AddComponent<OSCReceiver>();
        oscReceiver.LocalPort = localPort;

        // /gaze アドレスをバインド
        gazeBind = oscReceiver.Bind(gazeAddress, OnGazeMessageReceived);

        Debug.Log($"[OscGazeInput] Listening on port {localPort}, address: {gazeAddress}");
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
    }

    private void OnDestroy()
    {
       if (oscReceiver != null && gazeBind != null)
        {
            oscReceiver.Unbind(gazeBind);
        }
    }
}
