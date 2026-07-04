using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

// Self-guided tutorial for the Worker (Quest) — replaces the operator's verbal per-subject
// briefing. While ExperimentManager2 is in the Tutorial state this shows a paged WorldSpace
// panel the subject advances with the A button; the grip-practice page auto-advances when the
// subject actually grips a task QR (same 20 cm proximity rule as IdentificationTask, whose own
// grip handler stays inert during Tutorial because no target is assigned yet). The final page
// notifies the Expert (ExperimentManager2.NotifyTutorialComplete), whose UI then shows
// "completed — [Enter]" so no verbal hand-off is needed.
public class TutorialGuide : MonoBehaviour
{
    private ExperimentManager2 _manager;
    private QRSpatialManager   _qrManager;
    private Font               _font;

    private GameObject _panelGo;   // canvas root (parented to centerEyeAnchor, NOT to this object)
    private Text       _titleText;
    private Text       _bodyText;
    private Text       _hintText;
    private int        _page;
    private bool       _done;
    private float      _inputCooldownUntil;

    private const float k_proximityThreshold = 0.20f;   // matches IdentificationTask
    private const float k_pageCooldown       = 0.4f;    // A-button debounce between pages

#if UNITY_ANDROID && !UNITY_EDITOR
    private OVRCameraRig _ovrRig;
    private bool         _gripWasDown;
#endif

    private struct Page
    {
        public string Title;
        public string Body;
        public bool   GripPractice;
    }

    private static readonly Page[] k_pages =
    {
        new Page
        {
            Title = "これからの流れ",
            Body  = "実験は 10 セッションあります。\n\n" +
                    "各セッションの流れ:\n" +
                    "  音の時間 → 課題① 探す → 音の時間\n" +
                    "  → 課題② 組み立て → アンケート\n\n" +
                    "やることは毎回この画面と音声でお知らせします。",
        },
        new Page
        {
            Title = "実験者の「視線」が見えます",
            Body  = "課題中、実験者がいま見ている場所が\n" +
                    "線・円・四角い枠 のいずれかで表示されます。\n\n" +
                    "音声の指示とあわせて、視線を手がかりに\n" +
                    "対象を探してください。\n\n" +
                    "※視線が表示されないセッションもあります。",
        },
        new Page
        {
            Title = "練習: QR を選ぶ",
            Body  = "右手コントローラを近くの QR コードに近づけて\n" +
                    "（20cm 以内）、中指のグリップボタンを\n" +
                    "押してください。振動したら成功です。\n\n" +
                    "課題①では、実験者の視線が示す「正解の QR」を\n" +
                    "この操作で選びます。",
            GripPractice = true,
        },
        new Page
        {
            Title = "課題②とアンケート",
            Body  = "課題②では、視線が示す場所にブロックを\n" +
                    "組み立てて置きます（時間終了まで続けます）。\n\n" +
                    "各セッションの最後にアンケートが表示されます。\n" +
                    "質問に回答して送信してください。",
        },
        new Page
        {
            Title = "休憩と注意",
            Body  = "休憩ではヘッドセットを外して大丈夫です。\n" +
                    "再開するときは着け直してから声で合図してください。\n\n" +
                    "実験の途中で外したいときも、一声かけてください\n" +
                    "（外すと自動で一時停止します）。",
        },
    };

    public void Initialize(ExperimentManager2 manager)
    {
        _manager   = manager;
        _qrManager = Object.FindAnyObjectByType<QRSpatialManager>();
        _font = Resources.Load<Font>("Fonts/NotoSansJP-Regular")
             ?? Resources.Load<Font>("Fonts/NotoSansCJK-Regular")
             ?? Resources.Load<Font>("Fonts/NotoSansJP");

        _manager.OnStateChanged += HandleStateChanged;
        if (_manager.CurrentState == ExperimentState.Tutorial) ShowPanel();
    }

    private void OnDestroy()
    {
        if (_manager != null) _manager.OnStateChanged -= HandleStateChanged;
        if (_panelGo != null) Destroy(_panelGo);
    }

    private void HandleStateChanged(ExperimentState state)
    {
        if (state == ExperimentState.Tutorial) ShowPanel();
        else HidePanel();
    }

    // ── Panel lifecycle ─────────────────────────────────────────────────────

    private void ShowPanel()
    {
        if (_panelGo != null) return;

        var rig = Object.FindAnyObjectByType<OVRCameraRig>();
        Transform anchor = rig != null ? rig.centerEyeAnchor
                        : Camera.main != null ? Camera.main.transform : null;
        if (anchor == null)
        {
            Debug.LogWarning("[TutorialGuide] No camera anchor — tutorial panel unavailable.");
            return;
        }

        _page = 0;
        _done = false;
        _inputCooldownUntil = Time.unscaledTime + k_pageCooldown;

        var go = new GameObject("TutorialGuide_Canvas");
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

        _titleText = MakeText("Title", go.transform, new Vector2(0.04f, 0.84f), new Vector2(0.96f, 0.98f),
            "", 28, TextAnchor.MiddleCenter, new Color(0.7f, 0.9f, 1f));
        _bodyText  = MakeText("Body", go.transform, new Vector2(0.06f, 0.24f), new Vector2(0.94f, 0.82f),
            "", 21, TextAnchor.UpperLeft, new Color(0.92f, 0.94f, 0.97f));
        _hintText  = MakeText("Hint", go.transform, new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.20f),
            "", 21, TextAnchor.MiddleCenter, new Color(0.4f, 1f, 0.6f));

        Render();
    }

    private void HidePanel()
    {
        if (_panelGo != null) { Destroy(_panelGo); _panelGo = null; }
    }

    private void Render()
    {
        if (_panelGo == null) return;
        var p = k_pages[_page];
        _titleText.text = $"チュートリアル {_page + 1} / {k_pages.Length} — {p.Title}";
        _bodyText.text  = p.Body;
        _hintText.text  = p.GripPractice
            ? "QR を右グリップ（成功で自動的に次へ）\nうまくいかない場合は A ボタンでスキップ"
            : "A ボタンで次へ";
    }

    private void Complete()
    {
        _done = true;
        _manager?.NotifyTutorialComplete();
        FileLogger.Log("Tutorial", "[TutorialGuide] Worker finished all tutorial pages.");
        if (_titleText != null) _titleText.text = "説明はこれで終わりです";
        if (_bodyText  != null) _bodyText.text  = "そのままお待ちください。\nまもなく実験が始まります。";
        if (_hintText  != null)
        {
            _hintText.text  = "実験者の開始操作を待っています…";
            _hintText.color = new Color(0.8f, 0.85f, 0.9f);
        }
    }

    // ── Input ───────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_panelGo == null || _done) return;
        if (Time.unscaledTime < _inputCooldownUntil) return;

        if (k_pages[_page].GripPractice && PracticeGripSucceeded())
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            OvrHaptics.Pulse(this, 0.5f, 0.8f, 0.2f, OVRInput.Controller.RTouch);
#endif
            Advance();
            return;
        }

        if (AdvancePressed())
            Advance();
    }

    private void Advance()
    {
        _inputCooldownUntil = Time.unscaledTime + k_pageCooldown;
        if (_page + 1 >= k_pages.Length) { Complete(); return; }
        _page++;
        Render();
    }

    private bool AdvancePressed()
    {
#if UNITY_EDITOR
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) return true;
#endif
        return OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
    }

    private bool PracticeGripSucceeded()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        float grip        = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        bool  gripDown    = grip > OVRInputThresholds.Grip;
        bool  justPressed = gripDown && !_gripWasDown;
        _gripWasDown = gripDown;
        if (!justPressed) return false;
        // While X (left) is held, the right hand calibrates the mesh — don't count that as practice.
        if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) return false;
        if (_qrManager == null) return false;

        Vector3 controllerPos = GetRightControllerWorldPos();
        foreach (var kvp in _qrManager.DetectedMarkers)
        {
            if (kvp.Key.StartsWith(IdentificationTask.QR_CALIB_PREFIX)) continue;
            if (kvp.Value == null) continue;
            if (Vector3.Distance(controllerPos, kvp.Value.transform.position) < k_proximityThreshold)
            {
                FileLogger.Log("Tutorial", $"[TutorialGuide] Practice grip OK on '{kvp.Key}'.");
                return true;
            }
        }
        return false;
#else
        return false;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private Vector3 GetRightControllerWorldPos()
    {
        if (_ovrRig == null) _ovrRig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (_ovrRig != null) return _ovrRig.rightHandAnchor.position;
        return OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
    }
#endif

    // ── UI helpers ──────────────────────────────────────────────────────────

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
