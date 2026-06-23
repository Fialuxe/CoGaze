using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Photon.Pun;

#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Worker (Quest 3) side: detects QR codes via MRUK and broadcasts their
/// world-space pose to all Photon clients via RPC.
///
/// Expert (PC) side: receives the RPC and instantiates/updates a visual
/// marker GameObject at the reported position.
///
/// Attach this component to a networked Photon prefab that has a PhotonView.
/// </summary>
public class QRSpatialManager : MonoBehaviourPun
{
    [Header("Marker Visual")]
    [Tooltip("Prefab used to represent each detected QR marker in the Expert's view. " +
             "If null, a primitive sphere (scale 0.1 m) is created at runtime.")]
    [SerializeField] private GameObject markerPrefab;

    // markerId → instantiated GameObject
    private readonly Dictionary<string, GameObject> markerObjects = new();

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    /// <summary>Read-only view of all currently known markers (id → GameObject).</summary>
    public IReadOnlyDictionary<string, GameObject> DetectedMarkers => markerObjects;

    /// <summary>
    /// Fired on all clients whenever a marker is received (new or updated).
    /// Parameters: markerId, world position, world rotation.
    /// </summary>
    public event Action<string, Vector3, Quaternion> OnMarkerDetected;

    // ---------------------------------------------------------------
    // Unity lifecycle
    // ---------------------------------------------------------------

    private IEnumerator Start()
    {
        Debug.Log($"[QRSpatialManager] Start — platform={UnityEngine.Application.platform} viewID={photonView.ViewID}");
        // MRUK and the OVR QR tracker only exist on device; the Editor has no passthrough camera,
        // so the entire detection path is compiled out to keep Editor play-mode clean.
#if UNITY_ANDROID && !UNITY_EDITOR
        // Wait for MRUK singleton to initialize before configuring QR tracking
        float waited = 0f;
        while (MRUK.Instance == null && waited < 10f)
        {
            yield return null;
            waited += Time.deltaTime;
        }

        if (MRUK.Instance == null)
        {
            Debug.LogWarning($"[QRSpatialManager] MRUK.Instance still null after {waited:F2}s timeout. QR tracking will not start.");
            yield break;
        }

        Debug.Log($"[QRSpatialManager] MRUK ready after {waited:F2}s — enabling QR tracking.");
        EnableQRTracking();
#else
        Debug.Log("[QRSpatialManager] Non-Android: QR hardware inactive. Use SimulateQRDetection() via Inspector context menu to test.");
        yield break;
#endif
    }

    /// <summary>
    /// Editor/test: simulate a QR detection and broadcast via Photon RPC.
    /// Call via gear icon → SimulateQRDetection in the Inspector.
    /// </summary>
    [ContextMenu("SimulateQRDetection")]
    public void SimulateQRDetection()
    {
        string testId  = "TEST_QR_001";
        Vector3    pos = new Vector3(0f, 0f, 1f);
        Quaternion rot = Quaternion.identity;
        Debug.Log($"[QRSpatialManager] Simulating QR detection: id='{testId}'");
        FileLogger.Log("QRSpatialManager", $"[SIM] Simulated QR: id='{testId}' pos={pos}");

        if (PhotonNetwork.InRoom)
        {
            photonView.RPC(nameof(RPC_ReceiveQRMarker), RpcTarget.AllBuffered, testId, pos, rot);
        }
        else
        {
            Debug.LogWarning("[QRSpatialManager] SimulateQRDetection: not in a Photon room — calling RPC locally.");
            RPC_ReceiveQRMarker(testId, pos, rot);
        }
    }

    private void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        DisableQRTracking();
#endif
    }

    // ---------------------------------------------------------------
    // MRUK QR tracking — Android (Quest) only
    // ---------------------------------------------------------------

#if UNITY_ANDROID && !UNITY_EDITOR
    private void EnableQRTracking()
    {
        if (MRUK.Instance == null)
        {
            Debug.LogWarning("[QRSpatialManager] MRUK.Instance is null after waiting. QR tracking unavailable.");
            FileLogger.Log("QRSpatialManager", "ERROR: MRUK.Instance is null — QR tracking not started.");
            return;
        }

        Debug.Log("[QRSpatialManager] EnableQRTracking: configuring TrackerConfiguration...");
        MRUK.Instance.SceneSettings.TrackerConfiguration = new OVRAnchor.TrackerConfiguration
        {
            QRCodeTrackingEnabled = true
        };

        MRUK.Instance.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
        MRUK.Instance.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);

        // Trigger scene load now that TrackerConfiguration is set.
        // If LoadSceneFromDevice was already called before this, this is a no-op.
        Debug.Log("[QRSpatialManager] EnableQRTracking: calling LoadSceneFromDevice...");
        MRUK.Instance.LoadSceneFromDevice();
        Debug.Log("[QRSpatialManager] EnableQRTracking: LoadSceneFromDevice called. Listening for QR trackables.");
        FileLogger.Log("QRSpatialManager", "QR tracking enabled.");
    }

    private void DisableQRTracking()
    {
        if (MRUK.Instance == null) return;
        MRUK.Instance.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
        MRUK.Instance.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
    }

    private void OnTrackableAdded(MRUKTrackable trackable)
    {
        // Only handle QR / marker trackables (MarkerPayloadString is null for non-markers)
        if (trackable.MarkerPayloadString == null) return;

        string     markerId = trackable.MarkerPayloadString;
        Vector3    pos      = trackable.transform.position;
        Quaternion rot      = trackable.transform.rotation;

        // Immediate logcat feedback so the Worker can confirm detection on-device
        Debug.Log($"[QRSpatialManager] QR DETECTED on Worker: id='{markerId}' pos={pos} rot={rot.eulerAngles}");
        FileLogger.Log("QRSpatialManager", $"QR detected: id='{markerId}' pos={pos} rot={rot.eulerAngles}");

        // AllBuffered caches the RPC on Photon's server, so an Expert who joins after the
        // QR was scanned still receives the marker pose without requiring a manual resync.
        photonView.RPC(nameof(RPC_ReceiveQRMarker), RpcTarget.AllBuffered, markerId, pos, rot);
    }

    private void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.MarkerPayloadString != null)
            FileLogger.Log("QRSpatialManager", $"QR lost from tracking: id='{trackable.MarkerPayloadString}'" +
                           " (marker object remains in scene)");
    }
#endif

    // ---------------------------------------------------------------
    // Photon RPC — runs on ALL clients (Worker + Expert)
    // ---------------------------------------------------------------

    [PunRPC]
    private void RPC_ReceiveQRMarker(string markerId, Vector3 pos, Quaternion rot)
    {
        try
        {
            if (string.IsNullOrEmpty(markerId))
            {
                Debug.LogWarning("[QRSpatialManager] RPC_ReceiveQRMarker: received empty markerId, ignored.");
                return;
            }

            if (markerObjects.TryGetValue(markerId, out GameObject existing))
            {
                // Update pose of a previously known marker
                if (existing != null)
                    existing.transform.SetPositionAndRotation(pos, rot);
            }
            else
            {
                // First time we've seen this marker — create a visual
                GameObject marker = CreateMarkerObject(markerId, pos, rot);
                markerObjects[markerId] = marker;
            }

            Debug.Log($"[QRSpatialManager] Marker received: id='{markerId}' pos={pos}");
            FileLogger.Log("QRSpatialManager", $"RPC_ReceiveQRMarker id='{markerId}' pos={pos}");
            OnMarkerDetected?.Invoke(markerId, pos, rot);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[QRSpatialManager] RPC_ReceiveQRMarker error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // Worker-side utility: re-send all known markers (e.g. after reconnect)
    // ---------------------------------------------------------------

    /// <summary>
    /// Worker-side only: re-broadcasts every known marker so that a freshly
    /// joined or reconnected Expert receives the current state.
    /// </summary>
    public void ResyncAllMarkers()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        foreach (var kvp in markerObjects)
        {
            if (kvp.Value == null) continue;

            Vector3    pos = kvp.Value.transform.position;
            Quaternion rot = kvp.Value.transform.rotation;

            photonView.RPC(nameof(RPC_ReceiveQRMarker), RpcTarget.AllBuffered, kvp.Key, pos, rot);
            FileLogger.Log("QRSpatialManager", $"ResyncAllMarkers: resent '{kvp.Key}'");
        }
#else
        Debug.LogWarning("[QRSpatialManager] ResyncAllMarkers is a Worker-only operation " +
                         "and has no effect on the Expert / Editor.");
#endif
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private GameObject CreateMarkerObject(string markerId, Vector3 pos, Quaternion rot)
    {
        GameObject marker;

        if (markerPrefab != null)
        {
            marker = Instantiate(markerPrefab, pos, rot);
        }
        else
        {
            // Fallback: red sphere at 20 cm diameter (more visible than the original white 10 cm)
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.transform.SetPositionAndRotation(pos, rot);
            marker.transform.localScale = Vector3.one * 0.2f;

            // Apply red material so it stands out clearly on both Worker and Expert sides
            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                var mat = new Material(renderer.sharedMaterial);
                mat.color = Color.red;
                renderer.material = mat;
            }

            // Remove collider — these markers are purely visual
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        marker.name = $"QRMarker_{markerId}";

        AttachLabel(marker, markerId);

        return marker;
    }

    /// <summary>
    /// Adds a floating TextMeshPro label above the marker showing its ID.
    /// TMP is available via com.unity.ugui so no conditional compilation is needed.
    /// </summary>
    private static void AttachLabel(GameObject marker, string markerId)
    {
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(marker.transform, false);
        // Float slightly above the sphere surface
        labelGo.transform.localPosition = Vector3.up * 0.12f;

        var tmp = labelGo.AddComponent<TextMeshPro>();
        tmp.text               = markerId;
        tmp.fontSize           = 0.05f;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
    }
}
