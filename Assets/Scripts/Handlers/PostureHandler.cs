using UnityEngine;
using Photon.Pun;

/// <summary>
/// 頭部姿勢 (Position, Rotation) をPhotonStreamで同期するHandler。
/// IsMineの場合はIPostureInputからデータを読んでtransformを更新・送信し、
/// リモート側は補間してtransformに反映する。
/// </summary>
public class PostureHandler : MonoBehaviourPun, IPunObservable
{
    private IPostureInput postureInput;

    private Vector3 networkPosition;
    private Quaternion networkRotation;
    private float lerpSpeed = 10f;

    /// <summary>IPostureInput実装を注入する</summary>
    public void Initialize(IPostureInput input)
    {
        postureInput = input;
        Debug.Log($"[PostureHandler] Initialized with {input.GetType().Name}");
    }

    private void Update()
    {
        if (photonView.IsMine && postureInput != null)
        {
            // ローカル: HMDのトラッキングデータをtransformに反映
            transform.position = postureInput.Position;
            transform.rotation = postureInput.Rotation;
        }
        else if (!photonView.IsMine)
        {
            // リモート: 補間してスムーズに追従
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
