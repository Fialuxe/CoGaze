using System.Globalization;
using UnityEngine;
using Photon.Pun;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;

// Publishes Worker head/controller poses to Photon Custom Player Properties at ~15 Hz.
public class WorkerTrackingSync : MonoBehaviour
{
    // ── Tunables ─────────────────────────────────────────────────────────────

    [Tooltip("Maximum sends per second to Photon. Keep below 20.")]
    public float maxSendRate = 15f;

    [Tooltip("Position change threshold in metres.")]
    public float posThreshold = 0.01f;   // 1 cm

    [Tooltip("Forward direction change threshold (dot product). 0.999 ≈ 2.6°.")]
    public float fwdDotThreshold = 0.999f;

    // ── Runtime ───────────────────────────────────────────────────────────────

    private OVRCameraRig _rig;
    private float        _sendInterval;
    private float        _t;

    // Last-sent values for delta-gating
    private Vector3 _lastHPos  = Vector3.positiveInfinity;
    private Vector3 _lastHFwd  = Vector3.zero;
    private Vector3 _lastCtrl  = Vector3.positiveInfinity;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        _sendInterval = 1f / Mathf.Max(1f, maxSendRate);
#pragma warning disable CS0618
        _rig = Object.FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618
        if (_rig == null)
            Debug.LogWarning("[WorkerTrackingSync] OVRCameraRig not found — tracking data will not be sent.");
    }

    private void Update()
    {
        _t += Time.deltaTime;
        if (_t < _sendInterval) return;
        _t = 0f;

        if (!PhotonNetwork.InRoom) return;
        if (_rig == null) return;

        // ── Head ─────────────────────────────────────────────────────────────
        Transform eye = _rig.centerEyeAnchor;
        if (eye == null) return;

        Vector3 hPos = eye.position;
        Vector3 hFwd = eye.forward;

        // ── Controller ───────────────────────────────────────────────────────
        // Prefer right; fall back to left; skip if neither is tracked.
        bool rightValid = OVRInput.GetControllerPositionValid(OVRInput.Controller.RTouch);
        bool leftValid  = OVRInput.GetControllerPositionValid(OVRInput.Controller.LTouch);

        Vector3 ctrlPos  = Vector3.zero;
        bool    hasCtrl  = false;

        if (rightValid && _rig.rightControllerAnchor != null)
        {
            ctrlPos = _rig.rightControllerAnchor.position;
            hasCtrl = true;
        }
        else if (leftValid && _rig.leftControllerAnchor != null)
        {
            ctrlPos = _rig.leftControllerAnchor.position;
            hasCtrl = true;
        }

        // ── Change detection ─────────────────────────────────────────────────
        bool headMoved    = (hPos - _lastHPos).sqrMagnitude > posThreshold * posThreshold;
        bool headRotated  = Vector3.Dot(hFwd, _lastHFwd) < fwdDotThreshold;
        bool ctrlMoved    = hasCtrl && (ctrlPos - _lastCtrl).sqrMagnitude > posThreshold * posThreshold;
        bool ctrlLost     = !hasCtrl && _lastCtrl != Vector3.positiveInfinity;

        if (!headMoved && !headRotated && !ctrlMoved && !ctrlLost) return;

        // ── Build and send Hashtable (single call) ────────────────────────────
        var props = new Hashtable();

        if (headMoved || headRotated)
        {
            props["hPos"] = FmtVec(hPos);
            props["hFwd"] = FmtVec(hFwd);
            _lastHPos = hPos;
            _lastHFwd = hFwd;
        }

        if (ctrlMoved || ctrlLost)
        {
            props["rCtrl"] = hasCtrl ? FmtVec(ctrlPos) : "";
            _lastCtrl = hasCtrl ? ctrlPos : Vector3.positiveInfinity;
        }

        if (props.Count > 0)
            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // InvariantCulture ensures "x,y,z" stays unambiguous regardless of device locale.
    private static string FmtVec(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4}", v.x, v.y, v.z);
}
