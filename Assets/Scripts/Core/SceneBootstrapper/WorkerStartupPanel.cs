using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

// Worker (Quest) WorldSpace startup panel; A button confirms, Fatal checks block. Mirrors WorkerHUD2 canvas style.
public class WorkerStartupPanel : MonoBehaviour
{
    public bool Confirmed { get; private set; }

    private Font       _font;
    private Text       _hintText;
    private bool       _hasFatal;
    private GameObject _panelGo;   // canvas root (parented to centerEyeAnchor, NOT to this object)

    public void Initialize(StartupConfig config)
    {
        _font = Resources.Load<Font>("Fonts/NotoSansJP-Regular")
             ?? Resources.Load<Font>("Fonts/NotoSansCJK-Regular")
             ?? Resources.Load<Font>("Fonts/NotoSansJP");

        // Worker skips the instructions-file check (Android StreamingAssets isn't a readable File
        // path; the Expert authority covers it).
        var issues = StartupSelfCheck.Run(config.participantId, config.participantOrderIndex, includeInstructions: false);
        _hasFatal  = StartupSelfCheck.HasFatal(issues);

        Build(config, issues);
    }

    private void Build(StartupConfig config, List<StartupSelfCheck.Issue> issues)
    {
        var rig = Object.FindAnyObjectByType<OVRCameraRig>();
        Transform anchor = rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;
        if (anchor == null)
        {
            Debug.LogWarning("[WorkerStartupPanel] No camera anchor — cannot show VR panel.");
            Confirmed = !_hasFatal;   // can't show UI: don't strand a healthy run, but never auto-pass a Fatal
            return;
        }

        var go = new GameObject("WorkerStartupPanel_Canvas");
        _panelGo = go;
        go.transform.SetParent(anchor, false);
        go.transform.localPosition = new Vector3(0f, 0.05f, 1.0f);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one * 0.001f;

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(560f, 420f);

        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(go.transform, false);
        bgGo.AddComponent<Image>().color = new Color(0.04f, 0.06f, 0.20f, 0.92f);
        Stretch(bgGo.GetComponent<RectTransform>());

        var accent = new GameObject("Accent");
        accent.transform.SetParent(go.transform, false);
        accent.AddComponent<Image>().color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var art = accent.GetComponent<RectTransform>();
        art.anchorMin = new Vector2(0f, 0.985f); art.anchorMax = new Vector2(1f, 0.985f);
        art.offsetMin = new Vector2(0f, -2f);    art.offsetMax = new Vector2(0f, 2f);

        MakeText("Title", go.transform, new Vector2(0.04f, 0.86f), new Vector2(0.96f, 0.99f),
            "セットアップ準備", 30, TextAnchor.MiddleCenter, new Color(0.7f, 0.9f, 1f));

        string mode = config.offlineMode ? "オフライン" : "オンライン";
        MakeText("Config", go.transform, new Vector2(0.05f, 0.72f), new Vector2(0.95f, 0.85f),
            $"参加者: {config.participantId}    /    {mode}", 20, TextAnchor.MiddleLeft, new Color(0.8f, 0.85f, 0.9f));

        // Self-check rows (skip the long condition-order line — keep VR text readable)
        var sb = new StringBuilder();
        foreach (var iss in issues)
        {
            if (iss.Message.StartsWith("条件順")) continue;
            string pfx = iss.Severity == StartupSelfCheck.Severity.Fatal   ? "● "
                       : iss.Severity == StartupSelfCheck.Severity.Warning ? "▲ "
                                                                           : "✓ ";
            sb.AppendLine(pfx + iss.Message);
        }
        MakeText("SelfCheck", go.transform, new Vector2(0.05f, 0.28f), new Vector2(0.95f, 0.70f),
            sb.ToString(), 19, TextAnchor.UpperLeft, new Color(0.9f, 0.92f, 0.95f));

        _hintText = MakeText("Hint", go.transform, new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.25f),
            "", 22, TextAnchor.MiddleCenter, Color.white);
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_hintText == null) return;
        if (_hasFatal)
        {
            _hintText.text  = "起動前チェックに問題があります（● 赤）。\n解決してアプリを再起動してください。";
            _hintText.color = new Color(1f, 0.45f, 0.4f);
        }
        else
        {
            _hintText.text  = "準備ができたら 右コントローラの A ボタン で開始";
            _hintText.color = new Color(0.4f, 1f, 0.6f);
        }
    }

    private void Update()
    {
        if (Confirmed || _hasFatal) return;

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            Confirmed = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            OvrHaptics.Pulse(this, 0.5f, 0.8f, 0.2f, OVRInput.Controller.RTouch);
#endif
            if (_hintText != null) { _hintText.text = "開始します…"; _hintText.color = Color.white; }
            // The canvas lives under centerEyeAnchor (a different hierarchy), so destroy the stored
            // root explicitly — GetComponentInChildren on this object would never find it.
            if (_panelGo != null) Destroy(_panelGo, 0.4f);
        }
    }

    // Safety net: if the component is destroyed (e.g. SceneBootstrapper2 cleans it up) the canvas
    // is in a separate hierarchy and would otherwise linger.
    private void OnDestroy()
    {
        if (_panelGo != null) Destroy(_panelGo);
    }

    private Text MakeText(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                          string text, int fontSize, TextAnchor alignment, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.text               = text;
        t.fontSize           = fontSize;
        t.alignment          = alignment;
        t.color              = color;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow   = VerticalWrapMode.Overflow;

        Font f = _font;
        if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (f != null) t.font = f;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return t;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
