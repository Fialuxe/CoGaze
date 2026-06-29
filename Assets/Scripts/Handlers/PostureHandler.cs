using UnityEngine;
using Photon.Pun;

// Synchronises head pose (position, rotation) over Photon with remote-side interpolation.
public class PostureHandler : MonoBehaviourPun, IPunObservable
{
    private IPostureInput _postureInput;

    private Vector3 _networkPosition;
    // Must be identity, not default(Quaternion) = (0,0,0,0): a zero-magnitude quaternion
    // makes Quaternion.Lerp assert ("!CompareApproximately(aScalar, 0.0F)") every frame
    // until the first network update arrives.
    private Quaternion _networkRotation = Quaternion.identity;
    private bool _hasNetworkData;

    private const float k_lerpSpeed = 10f;

    public void Initialize(IPostureInput input)
    {
        _postureInput = input;
        Debug.Log($"[PostureHandler] Initialized with {input.GetType().Name}");
    }

    private void Update()
    {
        if (photonView.IsMine && _postureInput != null)
        {
            transform.position = _postureInput.Position;
            transform.rotation = _postureInput.Rotation;
        }
        else if (!photonView.IsMine && _hasNetworkData)
        {
            transform.position = Vector3.Lerp(
                transform.position, _networkPosition, Time.deltaTime * k_lerpSpeed);
            transform.rotation = Quaternion.Lerp(
                transform.rotation, _networkRotation, Time.deltaTime * k_lerpSpeed);
        }
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(transform.position);
            stream.SendNext(transform.rotation);
        }
        else
        {
            _networkPosition = (Vector3)stream.ReceiveNext();
            _networkRotation = (Quaternion)stream.ReceiveNext();
            _hasNetworkData = true;
        }
    }
}
