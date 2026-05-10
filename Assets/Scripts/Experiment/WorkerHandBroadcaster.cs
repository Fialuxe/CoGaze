using System;
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;

/// <summary>
/// Runs on the Worker (Meta Quest 3). Finds OVRSkeleton components automatically,
/// then broadcasts hand bone world positions to the Expert via Photon event 44
/// at ~30 fps — but only during Task steps (not Assembly).
/// Added via AddComponent in LocalWorkerSetup.Initialize().
/// </summary>
public class WorkerHandBroadcaster : MonoBehaviour
{
    private const byte HAND_EVENT     = 44;
    private const float SEND_INTERVAL = 1f / 30f;

    private ExperimentManager expManager;
    private bool              isSending;
    private float             sendTimer;

#if UNITY_ANDROID && !UNITY_EDITOR
    private OVRSkeleton leftSkeleton;
    private OVRSkeleton rightSkeleton;
#endif

    public void Initialize(ExperimentManager mgr)
    {
        expManager = mgr;
        expManager.OnStateChanged += OnStateChanged;

#if UNITY_ANDROID && !UNITY_EDITOR
        StartCoroutine(FindSkeletons());
#else
        Debug.Log("[WorkerHandBroadcaster] OVR hand tracking not available on this platform.");
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR

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
                    if (nameContainsLeft  && leftSkeleton  == null) leftSkeleton  = s;
                    if (nameContainsRight && rightSkeleton == null) rightSkeleton = s;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorkerHandBroadcaster] Skeleton search error: {ex.Message}");
            }

            if (leftSkeleton != null && rightSkeleton != null)
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
        if (!isSending) return;

        sendTimer += Time.deltaTime;
        if (sendTimer < SEND_INTERVAL) return;
        sendTimer = 0f;

        try { SendHandData(); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WorkerHandBroadcaster] Send error: {ex.Message}");
        }
    }

    private void SendHandData()
    {
        float[] leftBones  = PackBones(leftSkeleton);
        float[] rightBones = PackBones(rightSkeleton);

        object[] payload = { leftBones, rightBones };
        var opts = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        PhotonNetwork.RaiseEvent(HAND_EVENT, payload, opts, SendOptions.SendReliable);
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

    private void OnStateChanged(ExperimentState state)
    {
        // Only broadcast during identification Task steps — not Assembly (user requirement)
        isSending = state == ExperimentState.TaskRunning &&
                    expManager.CurrentStepType == StepType.Task;
#if UNITY_ANDROID && !UNITY_EDITOR
        sendTimer = 0f;
#endif
    }

    private void OnDestroy()
    {
        if (expManager != null) expManager.OnStateChanged -= OnStateChanged;
    }
}
