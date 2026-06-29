using UnityEngine;

// Reads HMD head-tracking via OVRCameraRig.centerEyeAnchor; falls back to Camera.main if rig not found.
public class MetaXRPostureInput : MonoBehaviour, IPostureInput
{
    private Transform _hmdTransform;

    public Vector3 Position => _hmdTransform != null ? _hmdTransform.position : Vector3.zero;
    public Quaternion Rotation => _hmdTransform != null ? _hmdTransform.rotation : Quaternion.identity;

    private void Start()
    {
        CacheHMDTransform();
    }

    private void Update()
    {
        if (_hmdTransform == null)
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
            _hmdTransform = rig.centerEyeAnchor;
            Debug.Log("[MetaXRPostureInput] Using OVRCameraRig.centerEyeAnchor");
            return;
        }
#endif
        // フォールバック: Camera.main
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            _hmdTransform = mainCam.transform;
            Debug.Log("[MetaXRPostureInput] Fallback: using Camera.main");
        }
        else
        {
            Debug.LogWarning("[MetaXRPostureInput] No OVRCameraRig or Camera.main found.");
        }
    }
}
