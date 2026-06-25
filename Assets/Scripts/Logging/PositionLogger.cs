using System.Text;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Periodically writes key world positions to the FileLogger for offline debugging:
/// the local head (center eye), every networked player object (Worker / Expert) with its role,
/// the shared "SharedMesh" anchor, and all current QR markers.
///
/// One instance is added per client by SceneBootstrapper2. Output lands in the same
/// cogaze_*.log under the [Pos] category, e.g.:
///   [Pos] head(worker)=(6.98,1.20,1.64) fwd=(0.12,-0.01,0.99) LocalWorker(Clone)/worker=(...) RemoteExpert(Clone)/expert=(...) SharedMesh=(...) QR[B]=(...)
/// </summary>
public class PositionLogger : MonoBehaviour
{
    [Tooltip("Seconds between log lines.")]
    public float interval = 1.0f;

    private float     _t;
    private Transform _head;
    private Transform _sharedMesh;
    private QRSpatialManager _qr;

    private void Start()
    {
#pragma warning disable CS0618
        var rig = Object.FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618
        _head = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
    }

    private void Update()
    {
        _t += Time.deltaTime;
        if (_t < interval) return;
        _t = 0f;

        var sb = new StringBuilder();

        if (_head != null) sb.Append($"head({RoleManager.LocalRole})={Fmt(_head.position)} fwd={Fmt(_head.forward)} ");

        // Networked players (the LocalWorker / RemoteExpert clones carry a PhotonView).
        foreach (var pv in FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
        {
            string n = pv.gameObject.name;
            if (!n.Contains("Worker") && !n.Contains("Expert")) continue;
            string role = pv.Owner != null ? RoleManager.GetPlayerRole(pv.Owner) : "?";
            sb.Append($"{n}/{role}={Fmt(pv.transform.position)} ");
        }

        if (_sharedMesh == null)
        {
            var sm = GameObject.Find("SharedMesh");
            if (sm != null) _sharedMesh = sm.transform;
        }
        if (_sharedMesh != null) sb.Append($"SharedMesh={Fmt(_sharedMesh.position)} ");

        if (_qr == null) _qr = FindAnyObjectByType<QRSpatialManager>();
        if (_qr != null)
            foreach (var kv in _qr.DetectedMarkers)
                if (kv.Value != null) sb.Append($"QR[{kv.Key}]={Fmt(kv.Value.transform.position)} ");

        if (sb.Length > 0) FileLogger.Log("Pos", sb.ToString());
    }

    private static string Fmt(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
}
