using UnityEngine;

/// <summary>
/// Meta XR SDK のアイトラッキングAPIを使用して視線データを取得する。
/// OVRPlugin.GetEyeGazesState() で両目の視線方向を取得し、
/// 正規化された (x, y, blink) 形式で返す。
/// </summary>
public class MetaXRGazeInput : MonoBehaviour, IGazeInput
{
    private Vector3 gazeData = new Vector3(0.5f, 0.5f, 0f);
    private bool isAvailable = false;

    public Vector3 GazeData => gazeData;
    public bool IsAvailable => isAvailable;

#if UNITY_ANDROID
    private OVRPlugin.EyeGazesState eyeGazesState;
#endif

    private void Update()
    {
#if UNITY_ANDROID
        UpdateEyeGaze();
#endif
    }

#if UNITY_ANDROID
    private void UpdateEyeGaze()
    {
        if (!OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref eyeGazesState))
        {
            isAvailable = false;
            gazeData.z = 1f; // トラッキング喪失: 直前の x,y を生きた注視として送らず blink=1 にする
            return;
        }

        var leftEye = eyeGazesState.EyeGazes[0];
        var rightEye = eyeGazesState.EyeGazes[1];

        if (!leftEye.IsValid && !rightEye.IsValid)
        {
            isAvailable = false;
            gazeData.z = 1f; // 両目とも無効: 直前の x,y を生きた注視として送らず blink=1 にする
            return;
        }

        // 有効な目のデータを使って視線方向を計算
        float confidence;

        if (leftEye.IsValid && rightEye.IsValid)
        {
            // 両目の平均
            Vector3 leftPos = leftEye.Pose.Position.FromFlippedZVector3f();
            Vector3 rightPos = rightEye.Pose.Position.FromFlippedZVector3f();
            Quaternion leftRot = leftEye.Pose.Orientation.FromFlippedZQuatf();
            Quaternion rightRot = rightEye.Pose.Orientation.FromFlippedZQuatf();

            Vector3 leftDir = leftRot * Vector3.forward;
            Vector3 rightDir = rightRot * Vector3.forward;
            Vector3 gazeDir = ((leftDir + rightDir) * 0.5f).normalized;

            confidence = (leftEye.Confidence + rightEye.Confidence) * 0.5f;
            ComputeNormalizedGaze(gazeDir, confidence);
        }
        else
        {
            var validEye = leftEye.IsValid ? leftEye : rightEye;
            Quaternion rot = validEye.Pose.Orientation.FromFlippedZQuatf();
            Vector3 gazeDir = rot * Vector3.forward;
            ComputeNormalizedGaze(gazeDir, validEye.Confidence);
        }

        isAvailable = true;
    }

    private void ComputeNormalizedGaze(Vector3 gazeDirection, float confidence)
    {
        // 視線方向を0-1の正規化座標に変換
        // gazeDirection.x: 左(-1)〜右(+1), gazeDirection.y: 下(-1)〜上(+1)
        // Quest3 eye tracking covers ~±40° FOV; sin(40°) ≈ 0.643
        const float kHalfFovSin = 0.6428f;
        float x = Mathf.InverseLerp(-kHalfFovSin, kHalfFovSin, gazeDirection.x);
        float y = Mathf.InverseLerp(-kHalfFovSin, kHalfFovSin, gazeDirection.y);

        // blink: confidence が低い場合をblinkとみなす
        float blink = confidence < 0.3f ? 1f : 0f;

        gazeData = new Vector3(
            Mathf.Clamp01(x),
            Mathf.Clamp01(y),
            blink
        );
    }
#endif
}
