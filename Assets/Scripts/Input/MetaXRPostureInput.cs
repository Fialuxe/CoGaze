using UnityEngine;

/// <summary>
/// Meta XR HMD の頭部トラッキングデータを取得する。
/// OVRCameraRig の CenterEyeAnchor を優先的に参照し、
/// 見つからない場合は Camera.main にフォールバックする。
/// </summary>
public class MetaXRPostureInput : MonoBehaviour, IPostureInput
{
    private Transform hmdTransform;

    public Vector3 Position => hmdTransform != null ? hmdTransform.position : Vector3.zero;
    public Quaternion Rotation => hmdTransform != null ? hmdTransform.rotation : Quaternion.identity;

    private void Start()
    {
        CacheHMDTransform();
    }

    private void Update()
    {
        if (hmdTransform == null)
        {
            CacheHMDTransform();
        }
    }

    private void CacheHMDTransform()
    {
#if UNITY_ANDROID
        // OVRCameraRig の CenterEyeAnchor を優先
        OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            hmdTransform = rig.centerEyeAnchor;
            Debug.Log("[MetaXRPostureInput] Using OVRCameraRig.centerEyeAnchor");
            return;
        }
#endif
        // フォールバック: Camera.main
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            hmdTransform = mainCam.transform;
            Debug.Log("[MetaXRPostureInput] Fallback: using Camera.main");
        }
        else
        {
            Debug.LogWarning("[MetaXRPostureInput] No OVRCameraRig or Camera.main found.");
        }
    }
}
