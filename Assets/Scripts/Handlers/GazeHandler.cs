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
            Vector3 data = gazeInput != null ? gazeInput.GazeData : Vector3.zero;
            stream.SendNext(data.x);
            stream.SendNext(data.y);
            stream.SendNext(data.z);
            stream.SendNext((int)CurrentMode);
        }
        else
        {
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
