using System;
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

// Runs on the Worker (Quest 3) and broadcasts hand bone world positions to the Expert via Photon event 44 at ~30 fps.
public class WorkerHandBroadcaster : MonoBehaviour
{
    private const byte  k_handEvent    = 44;
    private const float k_sendInterval = 1f / 30f;

    private ExperimentManager2 _expManager;
    private bool               _isSending;
    private float              _sendTimer;

    public void Initialize(ExperimentManager2 mgr)
    {
        _expManager = mgr;
        _expManager.OnStateChanged += OnStateChanged;

#if UNITY_ANDROID && !UNITY_EDITOR
        StartCoroutine(FindSkeletons());
#else
        Debug.Log("[WorkerHandBroadcaster] OVR hand tracking not available on this platform.");
#endif
    }

    private void OnStateChanged(ExperimentState state)
    {
        // Only broadcast during identification Task steps — not Assembly (user requirement)
        _isSending = state == ExperimentState.TaskRunning &&
                    _expManager.CurrentStepType == StepType.Task;
#if UNITY_ANDROID && !UNITY_EDITOR
        _sendTimer = 0f;
#endif
    }

    private void OnDestroy()
    {
        if (_expManager != null) _expManager.OnStateChanged -= OnStateChanged;
    }

    // ── Android (Quest 3) — not compiled for PC / Editor ─────────────────
#if UNITY_ANDROID && !UNITY_EDITOR

    private OVRSkeleton _leftSkeleton;
    private OVRSkeleton _rightSkeleton;

    private IEnumerator FindSkeletons()
    {
        const int maxAttempts = 10;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var skeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
                foreach (var s in skeletons)
                {
                    bool nameContainsLeft  = ContainsInHierarchy(s.transform, "Left");
                    bool nameContainsRight = ContainsInHierarchy(s.transform, "Right");
                    if (nameContainsLeft  && _leftSkeleton  == null) _leftSkeleton  = s;
                    if (nameContainsRight && _rightSkeleton == null) _rightSkeleton = s;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorkerHandBroadcaster] Skeleton search error: {ex.Message}");
            }

            if (_leftSkeleton != null && _rightSkeleton != null)
            {
                Debug.Log("[WorkerHandBroadcaster] Found both OVRSkeletons.");
                yield break;
            }

            yield return new WaitForSeconds(0.5f);
        }

        Debug.LogWarning("[WorkerHandBroadcaster] Could not find OVRSkeleton components after " +
                         $"{maxAttempts} attempts — hand data will not be broadcast.");
    }

    private static bool ContainsInHierarchy(Transform t, string keyword)
    {
        var current = t;
        while (current != null)
        {
            if (current.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            current = current.parent;
        }
        return false;
    }

    private void Update()
    {
        if (!_isSending) return;

        _sendTimer += Time.deltaTime;
        if (_sendTimer < k_sendInterval) return;
        _sendTimer = 0f;

        try { SendHandData(); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WorkerHandBroadcaster] Send error: {ex.Message}");
        }
    }

    private void SendHandData()
    {
        float[] leftBones  = PackBones(_leftSkeleton);
        float[] rightBones = PackBones(_rightSkeleton);

        object[] payload = { leftBones, rightBones };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(k_handEvent, payload, opts, SendOptions.SendReliable);
    }

    private static float[] PackBones(OVRSkeleton skeleton)
    {
        if (skeleton == null || !skeleton.IsInitialized ||
            skeleton.Bones == null || skeleton.Bones.Count < 24)
            return new float[0];

        var data = new float[72]; // 24 bones × 3 floats
        try
        {
            for (int i = 0; i < 24; i++)
            {
                Vector3 pos = skeleton.Bones[i].Transform.position;
                data[i * 3]     = pos.x;
                data[i * 3 + 1] = pos.y;
                data[i * 3 + 2] = pos.z;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WorkerHandBroadcaster] Bone read error: {ex.Message}");
            return new float[0];
        }

        return data;
    }

#endif // UNITY_ANDROID && !UNITY_EDITOR
}
