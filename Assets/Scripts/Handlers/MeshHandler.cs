using UnityEngine;
using Photon.Pun;

public class MeshHandler : MonoBehaviourPun
{
    [Header("Scene内の事前配置メッシュのオブジェクト名")]
    [SerializeField] private string meshObjectName = "SharedMesh";

    [Header("Calibration Settings")]
    [SerializeField] private float moveSpeed = 1.0f;
    [SerializeField] private float rotateSpeed = 45f;

    private GameObject meshObject;
    private bool isCalibrating = false;

    private void Start()
    {
        meshObject = GameObject.Find(meshObjectName);
        if (meshObject == null)
        {
            Debug.LogWarning($"[MeshHandler] Pre-placed mesh '{meshObjectName}' not found in scene.");
            return;
        }
        OptimizeMeshPerformance(meshObject);
    }

    private void OptimizeMeshPerformance(GameObject target)
    {
        foreach (var r in target.GetComponentsInChildren<MeshRenderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        int added = 0;
        foreach (var f in target.GetComponentsInChildren<MeshFilter>(true))
        {
            if (f.GetComponent<Collider>() == null)
            {
                f.gameObject.AddComponent<MeshCollider>();
                added++;
            }
        }
        if (added > 0) Debug.Log($"[MeshHandler] Added {added} MeshColliders.");

        var colliders = target.GetComponentsInChildren<MeshCollider>(true);
        if (colliders.Length > 0 && target.GetComponent<Rigidbody>() == null)
        {
            var rb = target.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    private void Update()
    {
        if (!photonView.IsMine || meshObject == null) return;
#if UNITY_ANDROID
        UpdateCalibration();
#endif
    }

#if UNITY_ANDROID
    private void UpdateCalibration()
    {
        // X button (left controller) — toggle mesh visibility
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
            ToggleMeshVisibility();

        // Y button (left controller) — input test
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            Debug.Log("[MeshHandler] Y button OK");

        // Right grip held → calibration mode
        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        if (!grip)
        {
            if (isCalibrating)
            {
                isCalibrating = false;
                Debug.Log("[MeshHandler] Calibration paused.");
            }
            return;
        }

        if (!isCalibrating)
        {
            isCalibrating = true;
            Debug.Log("[MeshHandler] Calibration active (right grip held).");
        }

        Vector2 stick   = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        bool    aHeld   = OVRInput.Get(OVRInput.Button.One,                OVRInput.Controller.RTouch);
        bool    trigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

        if (trigger)
        {
            // Trigger + stick Y → height
            if (Mathf.Abs(stick.y) > 0.1f)
                meshObject.transform.position += Vector3.up * stick.y * moveSpeed * Time.deltaTime;
        }
        else if (aHeld)
        {
            // A + stick X → Y-axis rotation
            if (Mathf.Abs(stick.x) > 0.1f)
                meshObject.transform.Rotate(Vector3.up, stick.x * rotateSpeed * Time.deltaTime, Space.World);
        }
        else if (stick.sqrMagnitude > 0.01f)
        {
            // Stick alone → XZ movement relative to HMD facing
            Transform hmd = Camera.main != null ? Camera.main.transform : transform;
            Vector3 fwd = Vector3.ProjectOnPlane(hmd.forward, Vector3.up).normalized;
            Vector3 rgt = Vector3.ProjectOnPlane(hmd.right,   Vector3.up).normalized;
            meshObject.transform.position += (fwd * stick.y + rgt * stick.x) * moveSpeed * Time.deltaTime;
        }

        // B button → confirm & send
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
        {
            SendMeshTransform();
            Debug.Log("[MeshHandler] Calibration confirmed and sent.");
        }
    }
#endif

    private void ToggleMeshVisibility()
    {
        var renderers = meshObject.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0) return;
        bool next = !renderers[0].enabled;
        foreach (var r in renderers) r.enabled = next;
        Debug.Log($"[MeshHandler] Mesh visibility → {next}");
    }

    public void SendMeshTransform()
    {
        if (meshObject == null) return;
        photonView.RPC(nameof(RPC_ReceiveMeshTransform), RpcTarget.AllBuffered,
            meshObject.transform.position,
            meshObject.transform.rotation,
            meshObject.transform.localScale);
    }

    [PunRPC]
    private void RPC_ReceiveMeshTransform(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        if (meshObject == null) meshObject = GameObject.Find(meshObjectName);
        if (meshObject == null) return;
        meshObject.transform.SetPositionAndRotation(pos, rot);
        meshObject.transform.localScale = scale;
    }
}
