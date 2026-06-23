using UnityEngine;
using Photon.Pun;

/// <summary>
/// Synchronises head pose (position, rotation) over Photon with remote-side interpolation.
/// </summary>
public class PostureHandler : MonoBehaviourPun, IPunObservable
{
    private IPostureInput postureInput;

    private Vector3 networkPosition;
    // Must be identity, not default(Quaternion) = (0,0,0,0): a zero-magnitude quaternion
    // makes Quaternion.Lerp assert ("!CompareApproximately(aScalar, 0.0F)") every frame
    // until the first network update arrives.
    private Quaternion networkRotation = Quaternion.identity;
    private float lerpSpeed = 10f;
    // Don't interpolate toward network pose until we've actually received one — otherwise
    // remote avatars visibly slide in from the world origin on spawn.
    private bool hasNetworkData = false;

    public void Initialize(IPostureInput input)
    {
        postureInput = input;
        Debug.Log($"[PostureHandler] Initialized with {input.GetType().Name}");
    }

    private void Update()
    {
        if (photonView.IsMine && postureInput != null)
        {
            transform.position = postureInput.Position;
            transform.rotation = postureInput.Rotation;
        }
        else if (!photonView.IsMine && hasNetworkData)
        {
            transform.position = Vector3.Lerp(
                transform.position, networkPosition, Time.deltaTime * lerpSpeed);
            transform.rotation = Quaternion.Lerp(
                transform.rotation, networkRotation, Time.deltaTime * lerpSpeed);
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
            networkPosition = (Vector3)stream.ReceiveNext();
            networkRotation = (Quaternion)stream.ReceiveNext();
            hasNetworkData = true;
        }
    }
}
