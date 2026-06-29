using UnityEngine;

// Pure 2-point yaw-constrained rigid registration for QR-based calibration (no Photon/OVR).
public static class DualQRCalibration
{
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
