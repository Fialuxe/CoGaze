using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Worker-only direct-touch questionnaire input; poke a button with fingertip or controller tip, no laser required.
public class QuestionnairePokeInput : MonoBehaviour
{
    [Tooltip("Distance (m) from the panel plane within which a poke counts as a press.")]
    public float touchDepth = 0.035f;

    [Tooltip("Distance (m) within which the panel stops following the head so it is easy to touch.")]
    public float engageDepth = 0.18f;

    private RectTransform _canvas;
    private OVRCameraRig  _rig;
    private OVRSkeleton[] _skeletons;

    private readonly List<Vector3> _pokePoints = new();
    // Per poke-point rising-edge tracking: the button it is currently pressing (null = none).
    private readonly Dictionary<int, Button> _pressing = new();

    public bool IsEngaged { get; private set; }

    public void Configure(RectTransform canvas, OVRCameraRig rig)
    {
        _canvas = canvas;
        _rig    = rig;
        RefreshSkeletons();
    }

    private void RefreshSkeletons()
    {
        _skeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
    }

    private void Update()
    {
        IsEngaged = false;
        if (_canvas == null) return;
        if (_skeletons == null || _skeletons.Length == 0) RefreshSkeletons();

        GatherPokePoints();
        if (_pokePoints.Count == 0) return;

        var     buttons  = _canvas.GetComponentsInChildren<Button>(false);
        Vector3 planePos = _canvas.position;
        Vector3 normal   = _canvas.forward;

        for (int i = 0; i < _pokePoints.Count; i++)
        {
            Vector3 p     = _pokePoints[i];
            float   depth = Mathf.Abs(Vector3.Dot(p - planePos, normal));
            if (depth < engageDepth) IsEngaged = true;

            Button over     = ButtonUnder(p, buttons);
            bool   touching = over != null && over.interactable && depth < touchDepth;

            _pressing.TryGetValue(i, out Button prev);
            if (touching && prev == null)          // rising edge → one press per poke-through
            {
                Press(over);
                _pressing[i] = over;
            }
            else if (!touching)
            {
                _pressing[i] = null;
            }
        }
    }

    private void GatherPokePoints()
    {
        _pokePoints.Clear();

        // Hand index fingertips (works with both the legacy and OpenXR skeleton bone sets).
        if (_skeletons != null)
        {
            foreach (var sk in _skeletons)
            {
                if (sk == null || !sk.IsInitialized || !sk.IsDataValid || sk.Bones == null) continue;
                foreach (var b in sk.Bones)
                {
                    if (b == null || b.Transform == null) continue;
                    if (b.Id == OVRSkeleton.BoneId.Hand_IndexTip || b.Id == OVRSkeleton.BoneId.XRHand_IndexTip)
                    {
                        _pokePoints.Add(b.Transform.position);
                        break;
                    }
                }
            }
        }

        // Controller tips (anchor nudged forward) so the panel can also be poked with a controller.
        if (_rig != null)
        {
            if (_rig.rightControllerAnchor != null && IsCtrl(OVRInput.Controller.RTouch))
                _pokePoints.Add(_rig.rightControllerAnchor.position + _rig.rightControllerAnchor.forward * 0.04f);
            if (_rig.leftControllerAnchor != null && IsCtrl(OVRInput.Controller.LTouch))
                _pokePoints.Add(_rig.leftControllerAnchor.position + _rig.leftControllerAnchor.forward * 0.04f);
        }
    }

    private static bool IsCtrl(OVRInput.Controller c)
        => (OVRInput.GetConnectedControllers() & c) == c;

    private static Button ButtonUnder(Vector3 worldPoint, Button[] buttons)
    {
        foreach (var b in buttons)
        {
            if (b == null) continue;
            var rt = b.transform as RectTransform;
            if (rt == null) continue;
            Vector3 local = rt.InverseTransformPoint(worldPoint);
            if (rt.rect.Contains(new Vector2(local.x, local.y)))
                return b;
        }
        return null;
    }

    private void Press(Button b)
    {
        b.onClick.Invoke();

        // onClick may call TeardownVRPointer which deactivates this GameObject.
        // StartCoroutine on an inactive object produces a Unity warning — guard here.
        if (!gameObject.activeInHierarchy) return;

        // Clear visual confirmation: briefly flash the button white, then restore the
        // post-click colour (e.g. the green "selected" tint set by the questionnaire).
        var img = b.GetComponent<Image>();
        if (img != null) StartCoroutine(FlashFeedback(img));

#if UNITY_ANDROID && !UNITY_EDITOR
        // Strong haptic pulse on the controllers (hands can't vibrate).
        OvrHaptics.Pulse(this, 0.6f, 0.9f, 0.08f, OVRInput.Controller.RTouch, OVRInput.Controller.LTouch);
#endif
    }

    private IEnumerator FlashFeedback(Image img)
    {
        if (img == null) yield break;
        Color restore = img.color;              // capture AFTER onClick so we restore the right state
        img.color = Color.white;
        yield return new WaitForSeconds(0.10f);
        if (img != null) img.color = restore;
    }

}
