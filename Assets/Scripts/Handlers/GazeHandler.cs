using UnityEngine;
using Photon.Pun;

/// <summary>
/// 視線データ (x, y, blink) をPhotonStreamで同期するHandler。
/// IsMineの場合はIGazeInputからデータを読んで送信し、
/// リモート側はReceivedGazeDataプロパティで受信データを参照する。
/// </summary>
public class GazeHandler : MonoBehaviourPun, IPunObservable
{
    private IGazeInput gazeInput;
    private Vector3 receivedGazeData;

    /// <summary>リモートから受信した視線データ (x, y, blink)</summary>
    public Vector3 ReceivedGazeData => receivedGazeData;

    /// <summary>ローカルの視線データ（IsMineの場合はgazeInputから直接）</summary>
    public Vector3 CurrentGazeData
    {
        get
        {
            if (photonView.IsMine && gazeInput != null)
                return gazeInput.GazeData;
            return receivedGazeData;
        }
    }

    /// <summary>現在の視線表示モード（Expert側で変更され、同期される）</summary>
    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Ray;

    /// <summary>IGazeInput実装を注入する</summary>
    public void Initialize(IGazeInput input)
    {
        gazeInput = input;
        Debug.Log($"[GazeHandler] Initialized with {input.GetType().Name}");
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            // ローカル: gazeInputからデータを読んで送信
            Vector3 data = gazeInput != null ? gazeInput.GazeData : Vector3.zero;
            stream.SendNext(data.x);
            stream.SendNext(data.y);
            stream.SendNext(data.z);
            stream.SendNext((int)CurrentMode);
        }
        else
        {
            // リモート: 受信データを保存
            float x = (float)stream.ReceiveNext();
            float y = (float)stream.ReceiveNext();
            float blink = (float)stream.ReceiveNext();
            receivedGazeData = new Vector3(x, y, blink);
            
            if (stream.PeekNext() is int modeInt)
            {
                CurrentMode = (VisualizationMode)stream.ReceiveNext();
            }
        }
    }
}
