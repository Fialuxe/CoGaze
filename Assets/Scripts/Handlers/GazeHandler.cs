using UnityEngine;
using Photon.Pun;

/// <summary>
/// Synchronises gaze data (x, y, blink) over Photon. The owner reads from IGazeInput
/// and sends; remotes receive and cache the value.
/// </summary>
public class GazeHandler : MonoBehaviourPun, IPunObservable
{
    private IGazeInput gazeInput;
    private Vector3 receivedGazeData;

    public Vector3 ReceivedGazeData => receivedGazeData;

    public Vector3 CurrentGazeData
    {
        get
        {
            if (photonView.IsMine && gazeInput != null)
                return gazeInput.GazeData;
            return receivedGazeData;
        }
    }

    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Ray;

    /// <summary>Inject IGazeInput implementation.</summary>
    public void Initialize(IGazeInput input)
    {
        gazeInput = input;
        Debug.Log($"[GazeHandler] Initialized with {input.GetType().Name}");
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            // 視線が利用不可（トラッキング喪失 / OSC ストリーム断）のときは、直前の有効値を
            // 流さず blink=1 の no-gaze を送る。古い注視を「生きた注視」として送信しない。
            Vector3 data = (gazeInput != null && gazeInput.IsAvailable)
                ? gazeInput.GazeData
                : new Vector3(0.5f, 0.5f, 1f);
            stream.SendNext(data.x);
            stream.SendNext(data.y);
            stream.SendNext(data.z);
            stream.SendNext((int)CurrentMode);
        }
        else
        {
            float x     = (float)stream.ReceiveNext();
            float y     = (float)stream.ReceiveNext();
            float blink = (float)stream.ReceiveNext();
            receivedGazeData = new Vector3(x, y, blink);
            CurrentMode = (VisualizationMode)(int)stream.ReceiveNext();
        }
    }
}
