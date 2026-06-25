using UnityEngine;

/// <summary>
/// Pure (no Photon/OVR) 2-point yaw-constrained rigid registration for QR-based calibration.
/// Separated from MonoBehaviour so it can be unit-tested in EditMode without Android build.
///
/// Gravity constraint: only yaw (rotation around Vector3.up) is solved.
/// No scale change. Both QR world positions are projected onto the horizontal plane for yaw,
/// so height differences between QR codes are safely ignored.
/// </summary>
public static class DualQRCalibration
{
    /// <summary>
    /// Computes the world (position, rotation) for SharedMesh so that indicatorA and indicatorB
    /// (in mesh-local space) align with the two detected QR world positions.
    /// </summary>
    /// <param name="aLocal">Indicator A in SharedMesh local space. Invariant under mesh motion
    ///   when the indicators are children of SharedMesh — use InverseTransformPoint or localPosition.</param>
    /// <param name="bLocal">Indicator B in SharedMesh local space.</param>
    /// <param name="aWorld">QR-A detected world position.</param>
    /// <param name="bWorld">QR-B detected world position.</param>
    /// <returns>(meshWorldPosition, meshWorldRotation) to apply via SetPositionAndRotation.</returns>
    public static (Vector3 position, Quaternion rotation) ComputePose(
        Vector3 aLocal, Vector3 bLocal, Vector3 aWorld, Vector3 bWorld)
    {
        // Project separation vectors onto the horizontal plane (ignore height difference).
        Vector3 vSource = Vector3.ProjectOnPlane(bLocal - aLocal, Vector3.up);
        Vector3 vTarget = Vector3.ProjectOnPlane(bWorld - aWorld, Vector3.up);

        // Degenerate guard: QRs almost directly above/below each other in world space,
        // or indicators stacked vertically in mesh-local space. Fall back to forward so
        // the result is NaN-free and deterministic.
        if (vSource.sqrMagnitude < 1e-6f) vSource = Vector3.forward;
        if (vTarget.sqrMagnitude < 1e-6f) vTarget = Vector3.forward;

        vSource.Normalize();
        vTarget.Normalize();

        float      yaw      = Vector3.SignedAngle(vSource, vTarget, Vector3.up);
        Quaternion rotation = Quaternion.AngleAxis(yaw, Vector3.up);

        // Align centroids: T = c_world - R * c_local
        Vector3 centLocal = (aLocal + bLocal) * 0.5f;
        Vector3 centWorld = (aWorld + bWorld) * 0.5f;
        Vector3 position  = centWorld - rotation * centLocal;

        return (position, rotation);
    }
}
