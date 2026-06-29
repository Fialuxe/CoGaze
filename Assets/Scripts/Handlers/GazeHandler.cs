using UnityEngine;
using Photon.Pun;

// Synchronises gaze data (x, y, blink) over Photon; owner reads from IGazeInput and sends, remotes receive and cache.
public class GazeHandler : MonoBehaviourPun, IPunObservable
{
    private IGazeInput _gazeInput;
    private Vector3 _receivedGazeData;

    public Vector3 ReceivedGazeData => _receivedGazeData;

    public Vector3 CurrentGazeData
    {
        get
        {
            if (photonView.IsMine && _gazeInput != null)
                return _gazeInput.GazeData;
            return _receivedGazeData;
        }
    }

    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Ray;

    public void Initialize(IGazeInput input)
    {
        _gazeInput = input;
        Debug.Log($"[GazeHandler] Initialized with {input.GetType().Name}");
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            // 視線が利用不可のときは blink=-1（フォールバック）を送る。blink=1（完全非表示）との区別を Worker 側で行う。
            Vector3 data = (_gazeInput != null && _gazeInput.IsAvailable)
                ? _gazeInput.GazeData
                : new Vector3(0.5f, 0.5f, -1f);  // blink=-1 = head-centre fallback sentinel (not hidden)
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
            _receivedGazeData = new Vector3(x, y, blink);
            CurrentMode = (VisualizationMode)(int)stream.ReceiveNext();
        }
    }
}
