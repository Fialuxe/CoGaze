using NUnit.Framework;
using UnityEngine;

/// <summary>
/// EditMode unit tests for DualQRCalibration.ComputePose.
/// Run via Window → General → Test Runner → EditMode.
///
/// These tests cover the pure math layer only. On-device behaviour (MRUK detection,
/// passthrough overlay, haptics) requires a physical Quest 3 build and cannot be
/// verified here.
/// </summary>
public class DualQRCalibrationTests
{
    private const float Tol = 0.001f;  // metre tolerance for position
    private const float DegTol = 0.5f; // degree tolerance for yaw

    // ── 1. Identity (no movement needed) ─────────────────────────────────────

    [Test]
    public void Identity_SourceEqualsTarget_ReturnsZeroPoseChange()
    {
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(1f, 0f, 0f);
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aL, bL);
        AssertVec3Near(Vector3.zero, pos, "position");
        AssertYawNear(0f, rot, "yaw");
    }

    // ── 2. Pure translation (no rotation) ────────────────────────────────────

    [Test]
    public void PureTranslation_NoRotation()
    {
        var aL     = new Vector3(0f, 0f, 0f);
        var bL     = new Vector3(1f, 0f, 0f);
        var offset = new Vector3(3f, 0f, -2f);
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aL + offset, bL + offset);
        AssertVec3Near(offset, pos, "position");
        AssertYawNear(0f, rot, "yaw");
    }

    // ── 3. Pure yaw 90° (constructed via AngleAxis to avoid manual sign errors) ──

    [Test]
    public void PureYaw90_ConstructedViaAngleAxis()
    {
        // Build world positions by applying AngleAxis(90, up) so the expected yaw is unambiguous.
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(1f, 0f, 0f);
        float yawDeg = 90f;
        var R = Quaternion.AngleAxis(yawDeg, Vector3.up);
        // AngleAxis(90,up)*(1,0,0) = (0,0,-1) in Unity's CW-positive convention.
        var aW = R * aL;  // (0, 0, 0)
        var bW = R * bL;  // (0, 0, -1)
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        AssertYawNear(yawDeg, rot, "yaw");
        AssertVec3Near(aW, pos + rot * aL, "A round-trip");
        AssertVec3Near(bW, pos + rot * bL, "B round-trip");
    }

    // ── 4. Yaw + translation: round-trip style, no hand-assumed sign ─────────

    [Test]
    public void YawAndTranslation_TransformPointRoundTrip()
    {
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(2f, 0f, 0f);
        float yawDeg = 90f;
        var T = new Vector3(1f, 0f, 1f);
        var R = Quaternion.AngleAxis(yawDeg, Vector3.up);
        var aW = T + R * aL;
        var bW = T + R * bL;
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        AssertYawNear(yawDeg, rot, "yaw");
        AssertVec3Near(aW, pos + rot * aL, "indicator A maps to QR-A world");
        AssertVec3Near(bW, pos + rot * bL, "indicator B maps to QR-B world");
    }

    // ── 5. Swap A/B gives same result (order-independence) ───────────────────

    [Test]
    public void SwapAB_ProducesSamePose()
    {
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(2f, 0f, 0f);
        var aW = new Vector3(1f, 0f, 0f);
        var bW = new Vector3(3f, 0f, 0f);
        var (pos1, rot1) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        var (pos2, rot2) = DualQRCalibration.ComputePose(bL, aL, bW, aW);
        AssertVec3Near(pos1, pos2, "position same after swap");
        Assert.AreEqual(rot1.eulerAngles.y, rot2.eulerAngles.y, DegTol, "yaw same after swap");
    }

    // ── 6. Height difference in world is ignored for yaw ─────────────────────

    [Test]
    public void HeightDifferenceBetweenQRs_DoesNotAffectYaw()
    {
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(1f, 0f, 0f);
        // QR-B is 0.5 m higher than QR-A in world, same XZ → should still give 0° yaw
        var aW = new Vector3(0f, 0.0f, 0f);
        var bW = new Vector3(1f, 0.5f, 0f);
        var (_, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        AssertYawNear(0f, rot, "yaw must ignore height difference");
    }

    // ── 7. Degenerate input (QRs stacked vertically) — must not NaN ─────────

    [Test]
    public void Degenerate_VerticalSeparationOnly_NoNaN()
    {
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(0f, 1f, 0f);  // purely vertical separation in local space
        var aW = new Vector3(1f, 0f, 0f);
        var bW = new Vector3(1f, 1f, 0f);
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        Assert.IsFalse(float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z), $"pos has NaN: {pos}");
        Assert.IsFalse(float.IsNaN(rot.x) || float.IsNaN(rot.y) || float.IsNaN(rot.z) || float.IsNaN(rot.w), $"rot has NaN: {rot}");
    }

    // ── 8. Realistic desk-corner scenario with known expected pose ────────────

    [Test]
    public void RealisticDeskCorners_KnownYawAndOffset()
    {
        // Physical setup: QR-A on desk 1 left corner, QR-B on desk 2.
        // Mesh-local indicator positions (design time, desk heights included).
        var aL = new Vector3(0f,   0.75f, 0f);
        var bL = new Vector3(1.5f, 0.75f, 2.0f);

        // Room calibration: 30° yaw, offset (2, 0, 1)
        float  yawDeg    = 30f;
        var    R_expected = Quaternion.AngleAxis(yawDeg, Vector3.up);
        var    T_expected = new Vector3(2f, 0f, 1f);

        // Simulate what QR detection would return: mesh-local positions → world
        var aW = T_expected + R_expected * aL;
        var bW = T_expected + R_expected * bL;

        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);

        AssertVec3Near(T_expected, pos, "mesh position matches room offset");
        AssertYawNear(yawDeg, rot, "mesh yaw matches room rotation");
    }

    // ── 9. Pure yaw 180° ─────────────────────────────────────────────────────

    [Test]
    public void PureYaw180_CentroidAtOrigin()
    {
        var aL = new Vector3(-1f, 0f, 0f);
        var bL = new Vector3( 1f, 0f, 0f);
        // Swap sides → 180° rotation, centroid stays at origin
        var aW = new Vector3( 1f, 0f, 0f);
        var bW = new Vector3(-1f, 0f, 0f);
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        AssertVec3Near(Vector3.zero, pos, "centroid at origin after 180°");
        float yaw = rot.eulerAngles.y;
        if (yaw > 180f) yaw -= 360f;
        Assert.AreEqual(180f, Mathf.Abs(yaw), DegTol, $"yaw must be ±180°, got {yaw}°");
    }

    // ── 10. Negative yaw (−45°) ───────────────────────────────────────────────

    [Test]
    public void NegativeYaw45_RoundTrip()
    {
        var aL = new Vector3(0f, 0f, 0f);
        var bL = new Vector3(1f, 0f, 0f);
        float  yawDeg    = -45f;
        var    R_expected = Quaternion.AngleAxis(yawDeg, Vector3.up);
        var    T_expected = new Vector3(-1f, 0f, 0.5f);
        var aW = T_expected + R_expected * aL;
        var bW = T_expected + R_expected * bL;
        var (pos, rot) = DualQRCalibration.ComputePose(aL, bL, aW, bW);
        AssertVec3Near(T_expected, pos, "negative-yaw position");
        AssertYawNear(yawDeg, rot, "negative yaw");
        AssertVec3Near(aW, pos + rot * aL, "indicator A round-trip");
        AssertVec3Near(bW, pos + rot * bL, "indicator B round-trip");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void AssertVec3Near(Vector3 expected, Vector3 actual, string msg)
    {
        Assert.AreEqual(expected.x, actual.x, Tol, $"{msg}.x  expected={expected} actual={actual}");
        Assert.AreEqual(expected.y, actual.y, Tol, $"{msg}.y  expected={expected} actual={actual}");
        Assert.AreEqual(expected.z, actual.z, Tol, $"{msg}.z  expected={expected} actual={actual}");
    }

    private void AssertYawNear(float expectedDeg, Quaternion rot, string msg)
    {
        float actual = rot.eulerAngles.y;
        if (actual > 180f) actual -= 360f;
        if (expectedDeg > 180f) expectedDeg -= 360f;
        float diff = Mathf.Abs(actual - expectedDeg);
        if (diff > 180f) diff = 360f - diff;
        Assert.IsTrue(diff < DegTol, $"{msg}: expected ~{expectedDeg}°, got {actual}° (diff={diff:F3}°)");
    }
}
