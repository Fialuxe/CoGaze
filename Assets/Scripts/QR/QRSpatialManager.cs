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

#if UNITY_ANDROID && !UNITY_EDITOR
    // markerId → live MRUK trackable. Kept so "QR init" can re-broadcast each QR at its CURRENT
    // pose (OVR keeps tracking them; MRUK does not re-fire OnTrackableAdded for known markers).
    private readonly Dictionary<string, Meta.XR.MRUtilityKit.MRUKTrackable> _trackables = new();
    private Coroutine _periodicBroadcastCoroutine;
#endif

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

        // Route through BroadcastMarker so the SharedMesh-relative conversion is applied
        // identically to the real detection path (in-room or local).
        BroadcastMarker(testId, pos, rot);
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

        _periodicBroadcastCoroutine = StartCoroutine(PeriodicBroadcastLoop());
    }

    private void DisableQRTracking()
    {
        if (MRUK.Instance == null) return;
        MRUK.Instance.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
        MRUK.Instance.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
        StopPeriodicBroadcast();
    }

    // 既知 trackable を 1 秒ごとに再ブロードキャストして検出むらを補う。
    // OnTrackableAdded は一度しか発火しないが MRUK は内部でポーズを更新し続けるため、
    // ここで現在座標を読み直して送ることで常に最新位置が全クライアントに届く。
    private IEnumerator PeriodicBroadcastLoop()
    {
        var wait = new WaitForSeconds(1f);
        while (true)
        {
            yield return wait;
            if (_trackables.Count == 0) continue;

            // コピーしてからイテレート（ループ中に _trackables が変更されても安全）
            var snapshot = new Dictionary<string, MRUKTrackable>(_trackables);
            foreach (var kvp in snapshot)
            {
                if (kvp.Value == null) continue;
                BroadcastMarker(kvp.Key,
                                kvp.Value.transform.position,
                                kvp.Value.transform.rotation,
                                buffered: false);   // AllBuffered は初回検出時のみ。ポーリングは All を使い蓄積を防ぐ
            }
            FileLogger.Log("QRSpatialManager", $"[Poll] re-broadcast {snapshot.Count} trackable(s).");
        }
    }

    [ContextMenu("Stop Periodic QR Broadcast")]
    public void StopPeriodicBroadcast()
    {
        if (_periodicBroadcastCoroutine == null) return;
        StopCoroutine(_periodicBroadcastCoroutine);
        _periodicBroadcastCoroutine = null;
        Debug.Log("[QRSpatialManager] Periodic QR broadcast stopped.");
        FileLogger.Log("QRSpatialManager", "Periodic QR broadcast stopped.");
    }

    [ContextMenu("Start Periodic QR Broadcast")]
    public void StartPeriodicBroadcast()
    {
        if (_periodicBroadcastCoroutine != null) return;  // already running
        _periodicBroadcastCoroutine = StartCoroutine(PeriodicBroadcastLoop());
        Debug.Log("[QRSpatialManager] Periodic QR broadcast restarted.");
        FileLogger.Log("QRSpatialManager", "Periodic QR broadcast restarted.");
    }

    private void OnTrackableAdded(MRUKTrackable trackable)
    {
        // Only handle QR / marker trackables (MarkerPayloadString is null for non-markers).
        // Diagnostic: a QR that is physically seen but whose payload failed to decode (e.g. a
        // floor code viewed at a grazing angle) arrives here with a null payload. Log it so we
        // can tell "never seen" apart from "seen but undecoded" instead of dropping it silently.
        if (trackable.MarkerPayloadString == null)
        {
            FileLogger.Log("QRSpatialManager",
                $"Trackable added with NULL payload (non-marker, or QR seen but undecoded) " +
                $"type={trackable.GetType().Name} pos={trackable.transform.position}");
            return;
        }

        string     markerId = trackable.MarkerPayloadString;
        Vector3    pos      = trackable.transform.position;
        Quaternion rot      = trackable.transform.rotation;

        // Immediate logcat feedback so the Worker can confirm detection on-device
        Debug.Log($"[QRSpatialManager] QR DETECTED on Worker: id='{markerId}' pos={pos} rot={rot.eulerAngles}");
        FileLogger.Log("QRSpatialManager", $"QR detected: id='{markerId}' pos={pos} rot={rot.eulerAngles}");

        _trackables[markerId] = trackable;   // keep the live trackable for QR-init re-broadcast

        // Broadcast in SharedMesh-relative space so the Expert places the marker correctly.
        BroadcastMarker(markerId, pos, rot);
    }

    private void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable.MarkerPayloadString != null)
        {
            _trackables.Remove(trackable.MarkerPayloadString);
            FileLogger.Log("QRSpatialManager", $"QR lost from tracking: id='{trackable.MarkerPayloadString}'" +
                           " (marker object remains in scene)");
        }
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

            // The pose was broadcast in SharedMesh-relative (local) space so that it lands
            // in the same place regardless of each client's tracking origin. Convert it back
            // to THIS client's world space using the local SharedMesh transform. If SharedMesh
            // is missing, fall back to treating the values as raw world coords (symmetric with
            // the sender's fallback in BroadcastMarker).
            Transform sharedMesh = GetSharedMesh();
            Vector3    worldPos;
            Quaternion worldRot;
            if (sharedMesh != null)
            {
                worldPos = sharedMesh.TransformPoint(pos);
                worldRot = sharedMesh.rotation * rot;
            }
            else
            {
                worldPos = pos;
                worldRot = rot;
            }

            if (markerObjects.TryGetValue(markerId, out GameObject existing))
            {
                // Update pose of a previously known marker
                if (existing != null)
                    existing.transform.SetPositionAndRotation(worldPos, worldRot);
            }
            else
            {
                // First time we've seen this marker — create a visual
                GameObject marker = CreateMarkerObject(markerId, worldPos, worldRot);
                markerObjects[markerId] = marker;
            }

            Debug.Log($"[QRSpatialManager] Marker received: id='{markerId}' worldPos={worldPos}");
            FileLogger.Log("QRSpatialManager", $"RPC_ReceiveQRMarker id='{markerId}' worldPos={worldPos}");
            // Hand subscribers (e.g. MeshHandler QR calibration) WORLD coords — it reads
            // rot.eulerAngles.y and expects world space.
            OnMarkerDetected?.Invoke(markerId, worldPos, worldRot);
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

            // Marker objects are stored in world space; BroadcastMarker re-applies the
            // world→SharedMesh-local conversion before sending.
            Vector3    pos = kvp.Value.transform.position;
            Quaternion rot = kvp.Value.transform.rotation;

            BroadcastMarker(kvp.Key, pos, rot);
            FileLogger.Log("QRSpatialManager", $"ResyncAllMarkers: resent '{kvp.Key}'");
        }
#else
        Debug.LogWarning("[QRSpatialManager] ResyncAllMarkers is a Worker-only operation " +
                         "and has no effect on the Expert / Editor.");
#endif
    }

    /// <summary>
    /// Worker-side: manually register a QR marker at a given world pose. For codes MRUK fails to
    /// auto-detect on Quest (slow/unreliable passthrough detection — e.g. floor codes seen at
    /// steep angles, or small/dense codes). The marker is
    /// stored and broadcast exactly like a detected one so the Expert sees it and IdentificationTask
    /// can match it — but it is deliberately NOT added to <c>_trackables</c> (it has no live MRUK
    /// pose), so it never consumes an MRUK tracking slot and PeriodicBroadcastLoop won't touch it.
    /// Re-calling with a known id overwrites its position (RPC_ReceiveQRMarker updates in place),
    /// so a mis-placed marker can be corrected by touching + gripping again.
    /// </summary>
    public void RegisterManualMarker(string markerId, Vector3 worldPos, Quaternion worldRot)
    {
        if (string.IsNullOrEmpty(markerId)) return;
        Debug.Log($"[QRSpatialManager] Manual marker register: id='{markerId}' worldPos={worldPos}");
        FileLogger.Log("QRSpatialManager", $"Manual register: id='{markerId}' worldPos={worldPos}");
        BroadcastMarker(markerId, worldPos, worldRot);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    // Cached reference to the shared spatial anchor ("SharedMesh"). Both Worker and Expert
    // keep this GameObject's transform in sync via MeshHandler.RPC_ReceiveMeshTransform, so it
    // is the common frame of reference for placing markers consistently across clients.
    private const string SharedMeshName = "SharedMesh";
    private Transform _sharedMesh;

    private Transform GetSharedMesh()
    {
        // Re-find if never resolved or if the cached object was destroyed.
        if (_sharedMesh == null)
        {
            var go = GameObject.Find(SharedMeshName);
            _sharedMesh = go != null ? go.transform : null;
        }
        return _sharedMesh;
    }

    /// <summary>
    /// Converts a world-space marker pose into SharedMesh-relative (local) space and
    /// broadcasts it. Expressing the pose relative to the shared anchor means it lands at the
    /// same physical spot on every client regardless of differing tracking origins (the cause
    /// of the Expert "weird location" bug). If SharedMesh is unavailable, the raw world pose is
    /// sent as a fallback (the receiver applies the matching fallback).
    /// </summary>
    // buffered=true（デフォルト）: AllBuffered — 遅延参加の Expert にも届く（初回検出時に使用）
    // buffered=false: All のみ — ポーリング再送時に使用（Photon バッファへの蓄積を防ぐ）
    private void BroadcastMarker(string markerId, Vector3 worldPos, Quaternion worldRot, bool buffered = true)
    {
        Transform sharedMesh = GetSharedMesh();
        Vector3    sendPos;
        Quaternion sendRot;
        if (sharedMesh != null)
        {
            sendPos = sharedMesh.InverseTransformPoint(worldPos);
            sendRot = Quaternion.Inverse(sharedMesh.rotation) * worldRot;
        }
        else
        {
            Debug.LogWarning("[QRSpatialManager] BroadcastMarker: 'SharedMesh' not found — " +
                             "sending raw world pose (Expert placement may be misaligned).");
            sendPos = worldPos;
            sendRot = worldRot;
        }

        var target = buffered ? RpcTarget.AllBuffered : RpcTarget.All;
        if (PhotonNetwork.InRoom)
        {
            photonView.RPC(nameof(RPC_ReceiveQRMarker), target, markerId, sendPos, sendRot);
        }
        else
        {
            Debug.LogWarning("[QRSpatialManager] BroadcastMarker: not in a Photon room — calling RPC locally.");
            RPC_ReceiveQRMarker(markerId, sendPos, sendRot);
        }
    }

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

            // Apply a red, URP-safe material so the sphere is reliably visible on Quest.
            // The primitive's default sharedMaterial is the Standard/Lit material, which under
            // URP often renders as invisible or magenta; building from a URP-compatible shader
            // (with non-URP fallbacks) avoids that.
            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material = CreateMarkerMaterial(Color.red);

            // Remove collider — these markers are purely visual
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }

        marker.name = $"QRMarker_{markerId}";

        // Labelling must never prevent the marker from being created/registered.
        // AttachLabel is already crash-proof, but wrap defensively as a last resort.
        try
        {
            AttachLabel(marker, markerId);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[QRSpatialManager] AttachLabel failed for '{markerId}' (marker kept): {ex.Message}");
        }

        return marker;
    }

    /// <summary>
    /// Builds a flat, unlit, single-colour material from a URP-safe shader so markers
    /// render correctly under the Universal Render Pipeline on Quest. Falls back to the
    /// built-in unlit shaders when URP is not present. Sets both <c>_BaseColor</c> (URP)
    /// and <c>_Color</c> (built-in) so the tint applies regardless of which shader is used.
    /// </summary>
    private static Material CreateMarkerMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Standard"); // last-ditch: always present

        var mat = new Material(shader);

        // URP/Unlit exposes _BaseColor; built-in unlit/Standard expose _Color.
        // Set whichever the chosen shader actually has (HasProperty guards both).
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color", color);
        // mat.color is a convenience accessor for _Color; harmless if absent.
        mat.color = color;

        return mat;
    }

    /// <summary>
    /// Adds a floating ID label above the marker.
    ///
    /// Uses <see cref="TextMeshPro"/> when TMP is available, but TMP throws a
    /// NullReferenceException on builds where TMP_Settings/essentials were never imported
    /// (common on the Quest player). In that case we fall back to a legacy 3D
    /// <see cref="TextMesh"/>, which needs no TMP_Settings. Either way this method must not
    /// throw — the marker is more important than its label.
    /// </summary>
    private static void AttachLabel(GameObject marker, string markerId)
    {
        // If TMP settings aren't baked into the build, AddComponent<TextMeshPro>() crashes
        // inside TMP_Settings. Detect that up front and use the legacy text path instead.
        bool tmpAvailable = TMP_Settings.instance != null;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(marker.transform, false);
        // Float slightly above the sphere surface
        labelGo.transform.localPosition = Vector3.up * 0.12f;

        if (tmpAvailable)
        {
            var tmp = labelGo.AddComponent<TextMeshPro>();
            tmp.text             = markerId;
            tmp.fontSize         = 0.05f;
            tmp.alignment        = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        }
        else
        {
            // Legacy fallback — no TMP_Settings dependency, so it can't hit the crash.
            var textMesh = labelGo.AddComponent<TextMesh>();
            textMesh.text          = markerId;
            textMesh.characterSize = 0.05f;
            textMesh.fontSize      = 64;
            textMesh.anchor        = TextAnchor.MiddleCenter;
            textMesh.alignment     = TextAlignment.Center;
            textMesh.color         = Color.white;
        }
    }
}
