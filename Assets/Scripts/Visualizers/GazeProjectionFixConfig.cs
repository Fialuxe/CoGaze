using UnityEngine;

// Scene marker that enables the Assembly-task gaze projection fixes. Present ONLY in
// ExperimentScene_GazeFix — when this component is absent (original ExperimentScene),
// GazeVisualizer / WorkerVideoStream keep their legacy behaviour bit-for-bit.
//
// Fix 1 (remapPillarbox):   Tobii gaze x is normalised over the whole Expert monitor, but the
//                           4:3 PCA video is pillarboxed inside the 16:9 screen — remap x from
//                           screen-space to video-space before ray reconstruction.
// Fix 2 (useRealIntrinsics): replace the guessed FOV=90°/4:3 with the real PCA intrinsics
//                           (focal length / principal point / crop) queried on the Quest.
// Fix 3 (usePcaPoseOrigin): reconstruct the gaze ray from the Worker-local PCA camera pose
//                           (left passthrough camera, at frame timestamp) instead of the
//                           Photon round-tripped RemoteExpert transform.
public class GazeProjectionFixConfig : MonoBehaviour
{
    [Header("Fix 1 — pillarbox x remap")]
    public bool remapPillarbox = true;
    [Tooltip("Aspect ratio of the Expert monitor that Tobii normalises gaze against.")]
    public float expertScreenAspect = 16f / 9f;

    [Header("Fix 2 — real PCA intrinsics")]
    public bool useRealIntrinsics = true;

    [Header("Fix 3 — local PCA pose as ray origin")]
    public bool usePcaPoseOrigin = true;

    public static GazeProjectionFixConfig Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[GazeProjectionFixConfig] Duplicate instance — keeping the first one.");
            return;
        }
        Instance = this;
        Debug.Log($"[GazeProjectionFixConfig] Gaze fixes active: pillarbox={remapPillarbox} " +
                  $"intrinsics={useRealIntrinsics} pcaPose={usePcaPoseOrigin} screenAspect={expertScreenAspect:F3}");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
