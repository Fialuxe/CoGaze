using UnityEngine;
using Photon.Pun;

/// <summary>
/// Synchronises head pose (position, rotation) over Photon with remote-side interpolation.
/// </summary>
public class PostureHandler : MonoBehaviourPun, IPunObservable
{
    private IPostureInput postureInput;

    private Vector3 networkPosition;
    private Quaternion networkRotation;
    private float lerpSpeed = 10f;

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
        else if (!photonView.IsMine)
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
        }
    }
}
