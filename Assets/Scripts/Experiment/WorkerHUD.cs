using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;

/// <summary>
/// World-space HUD for the Local Worker (Quest HMD).
/// Shows: connection status, current task state, countdown timer.
/// Parented to OVRCameraRig.centerEyeAnchor — follows the head.
///
/// Timer is polled directly from ExperimentManager.RemainingSeconds every
/// frame in Update() — this guarantees display regardless of whether the
/// OnTimerUpdated event fires (coroutines, network, etc.).
/// </summary>
public class WorkerHUD : MonoBehaviour
{
    [Header("Optional — assign a font with Japanese glyphs")]
    public Font japaneseFont;

    [Header("HUD position relative to center eye (metres)")]
    public Vector3 hudOffset  = new Vector3(-0.30f, 0.3f, 0.7f);
    public Vector2 hudSizeMm  = new Vector2(240f, 92f);
    public float   hudScaleM  = 0.001f;

    [Header("Alert Marker")]
    public float alertDistance  = 1.0f;
    public float lookAtAngleDeg = 15f;

    // ── HUD refs ──────────────────────────────────────────────────────────
    private Image backgroundImage;
    private Text  connStatusText;   // top row
    private Text  stateText;        // middle row
    private Text  timerText;        // bottom row

    // ── Alert ─────────────────────────────────────────────────────────────
    private GameObject alertMarkerGo;
    private bool       alertActive        = false;
    private bool       taskTimerExpired   = false;
    private float      alertActivatedTime = -1f;

    private Transform         cameraAnchor;
    private ExperimentManager manager;

    // ── Init ──────────────────────────────────────────────────────────────

    public void Initialize(ExperimentManager experimentManager)
    {
        manager = experimentManager;
        BuildHUD();

        manager.OnStateChanged       += HandleStateChanged;
        manager.OnInstructionChanged += HandleInstructionChanged;

        // Trigger initial UI state
        if (manager != null)
            HandleStateChanged(manager.CurrentState);
    }

    private void OnDestroy()
    {
        if (manager != null)
        {
            manager.OnStateChanged       -= HandleStateChanged;
            manager.OnInstructionChanged -= HandleInstructionChanged;
        }
    }

    // ── Update — polls timer and alert billboard every frame ──────────────

    private void Update()
    {
        if (manager == null) return;

        RefreshConnectionStatus();
        RefreshTimer();             // ← always poll; never rely solely on events
        RefreshAlertBillboard();
    }

    // ── Connection status ─────────────────────────────────────────────────

    private void RefreshConnectionStatus()
    {
        if (connStatusText == null) return;

        if (!PhotonNetwork.IsConnected)
        {
            connStatusText.text  = "⚠ 切断中...";
            connStatusText.color = Color.red;
            return;
        }

        bool expertOnline = false;
        foreach (var p in PhotonNetwork.PlayerListOthers)
        {
            if (RoleManager.GetPlayerRole(p) == RoleManager.ROLE_EXPERT)
            { expertOnline = true; break; }
        }

        if (expertOnline)
        {
            connStatusText.text  = "● Expert 接続済";
            connStatusText.color = new Color(0.3f, 1f, 0.5f);
        }
        else
        {
            connStatusText.text  = "○ Expert 待機中";
            connStatusText.color = Color.yellow;
        }
    }

    // ── Timer — direct poll ───────────────────────────────────────────────

    private void RefreshTimer()
    {
        if (timerText == null || manager == null) return;

        float rem   = manager.RemainingSeconds;
        var   state = manager.CurrentState;

        switch (state)
        {
            case ExperimentState.WhiteNoise:
                timerText.text  = FormatTime(rem);
                timerText.color = Color.yellow;
                break;

            case ExperimentState.TaskRunning:
                timerText.text  = FormatTime(rem);
                timerText.color = rem < 30f ? Color.red : Color.white;

                // Alert when timer hits 0
                if (rem <= 0f && !taskTimerExpired)
                {
                    taskTimerExpired = true;
                    ShowAlert();
                }
                break;

            // Static / gate states — do not overwrite the fixed text set by HandleStateChanged
            default:
                break;
        }
    }

    // ── Alert billboard ───────────────────────────────────────────────────

    private void RefreshAlertBillboard()
    {
        if (!alertActive || cameraAnchor == null || alertMarkerGo == null) return;
        if (Time.time - alertActivatedTime < 0.5f) return;

        Vector3 d = (cameraAnchor.position - alertMarkerGo.transform.position).normalized;
        if (d != Vector3.zero)
            alertMarkerGo.transform.rotation = Quaternion.LookRotation(d, Vector3.up);

        Vector3 toMarker = (alertMarkerGo.transform.position - cameraAnchor.position).normalized;
        if (Vector3.Angle(cameraAnchor.forward, toMarker) < lookAtAngleDeg)
            DismissAlert();
    }

    // ── Canvas Construction ───────────────────────────────────────────────

    private void BuildHUD()
    {
#pragma warning disable CS0618
        var rig = Object.FindObjectOfType<OVRCameraRig>();
#pragma warning restore CS0618
        cameraAnchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;
        if (cameraAnchor == null)
        {
            Debug.LogWarning("[WorkerHUD] No camera anchor found — HUD will not be shown.");
            return;
        }

        var go = new GameObject("WorkerHUD_Canvas");
        go.transform.SetParent(cameraAnchor, false);
        go.transform.localPosition = hudOffset;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one * hudScaleM;

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        go.GetComponent<RectTransform>().sizeDelta = hudSizeMm;

        // Background
        var bgGo = new GameObject("BG");
        bgGo.transform.SetParent(go.transform, false);
        backgroundImage = bgGo.AddComponent<Image>();
        backgroundImage.color = new Color(0.04f, 0.06f, 0.20f, 0.78f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        // Left accent bar (SAO style)
        var accent    = new GameObject("Accent");
        accent.transform.SetParent(go.transform, false);
        var accentImg = accent.AddComponent<Image>();
        accentImg.color = new Color(0.3f, 0.75f, 1f, 0.9f);
        var art = accent.GetComponent<RectTransform>();
        art.anchorMin = Vector2.zero; art.anchorMax = new Vector2(0f, 1f);
        art.offsetMin = Vector2.zero; art.offsetMax = new Vector2(4f, 0f);

        // Row 1 — connection status (top ~25 %)
        connStatusText = MakeText("ConnStatus", go.transform,
            new Vector2(0.05f, 0.74f), new Vector2(0.98f, 0.98f),
            "● 接続確認中...", 18, TextAnchor.MiddleLeft, Color.yellow);

        // Row 2 — task state (middle ~35 %)
        stateText = MakeText("StateText", go.transform,
            new Vector2(0.05f, 0.38f), new Vector2(0.98f, 0.76f),
            "待機中...", 22, TextAnchor.MiddleLeft, new Color(0.6f, 0.9f, 1f));

        // Row 3 — timer (bottom ~38 %)  fontSize kept at 28 to fit the area
        timerText = MakeText("TimerText", go.transform,
            new Vector2(0.05f, 0.02f), new Vector2(0.98f, 0.40f),
            "--:--", 28, TextAnchor.MiddleLeft, Color.white);

        // Dividers
        MakeDivider(go.transform, 0.375f);
        MakeDivider(go.transform, 0.74f);

        BuildAlertMarker();

        Debug.Log("[WorkerHUD] HUD built successfully.");
    }

    private void MakeDivider(Transform parent, float yAnchor)
    {
        var go  = new GameObject("Divider");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.3f, 0.75f, 1f, 0.25f);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.03f, yAnchor);
        rt.anchorMax = new Vector2(0.97f, yAnchor);
        rt.offsetMin = new Vector2(0f, -0.5f);
        rt.offsetMax = new Vector2(0f,  0.5f);
    }

    private void BuildAlertMarker()
    {
        if (cameraAnchor == null) return;
        alertMarkerGo = new GameObject("WorkerHUD_AlertMarker");
        alertMarkerGo.transform.position = cameraAnchor.position + cameraAnchor.forward;

        var mc = alertMarkerGo.AddComponent<Canvas>();
        mc.renderMode = RenderMode.WorldSpace;
        alertMarkerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(160f, 160f);
        alertMarkerGo.transform.localScale = Vector3.one * 0.002f;

        var bgGo = new GameObject("AlertBG");
        bgGo.transform.SetParent(alertMarkerGo.transform, false);
        bgGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.18f);
        StretchFill(bgGo.GetComponent<RectTransform>());

        MakeText("Excl", alertMarkerGo.transform,
            new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f),
            "!", 110, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.1f));

        alertMarkerGo.SetActive(false);
    }

    // ── State Handler ─────────────────────────────────────────────────────

    private void HandleStateChanged(ExperimentState state)
    {
        if (state != ExperimentState.TaskRunning)
        {
            taskTimerExpired = false;
            DismissAlert();
        }

        switch (state)
        {
            case ExperimentState.Idle:
                SetState("準備中...", new Color(1f, 0.85f, 0.2f));
                SetTimer("--:--", Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.Ready:
                SetState("開始を待っています", Color.green);
                SetTimer("--:--", Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.WhiteNoise:
                SetState("インターバル中", Color.yellow);
                // Timer row will be updated by RefreshTimer() every frame
                SetTimer(FormatTime(manager.RemainingSeconds), Color.yellow);
                SetPanelMode(true);
                break;

            case ExperimentState.TaskRunning:
                SetState("タスク実行中", new Color(0.6f, 0.9f, 1f));
                // Timer row will be updated by RefreshTimer() every frame
                SetTimer(FormatTime(manager.RemainingSeconds), Color.white);
                SetPanelMode(true);    // keep instruction row visible so Worker sees the task instruction
                break;

            case ExperimentState.TaskComplete:
                SetState("タスク終了\n次へ進んでください", new Color(1f, 0.65f, 0.15f));
                SetTimer("00:00", Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.NoiseComplete:
                SetState("インターバル終了\n次へ進んでください", new Color(1f, 0.65f, 0.15f));
                SetTimer("00:00", Color.yellow);
                SetPanelMode(true);
                break;

            case ExperimentState.Questionnaire:
                SetState("アンケート記入中", new Color(0.4f, 0.8f, 1f));
                SetTimer("--:--", Color.white);
                SetPanelMode(true);
                break;

            case ExperimentState.Finished:
                SetState("実験終了\nありがとうございました", Color.cyan);
                SetTimer("--:--", Color.gray);
                SetPanelMode(true);
                break;
        }
    }

    // ── Instruction Handler ───────────────────────────────────────────────

    private void HandleInstructionChanged(string instruction)
    {
        // Override the default state label with the step-specific local instruction.
        // Empty string means no instruction defined for this step — keep the state label.
        if (!string.IsNullOrEmpty(instruction))
            SetState(instruction, new Color(0.6f, 0.9f, 1f));
    }

    // ── Panel Mode ────────────────────────────────────────────────────────

    private void SetPanelMode(bool full)
    {
        if (backgroundImage != null)
            backgroundImage.color = full
                ? new Color(0.04f, 0.06f, 0.20f, 0.78f)
                : new Color(0.02f, 0.02f, 0.08f, 0.40f);

        if (stateText      != null) stateText.enabled      = full;
        if (connStatusText != null) connStatusText.enabled  = full;
        // timerText is always visible
    }

    // ── Alert ─────────────────────────────────────────────────────────────

    private void ShowAlert()
    {
        if (alertMarkerGo == null || cameraAnchor == null) return;

        Vector3 fwd   = Vector3.ProjectOnPlane(cameraAnchor.forward, Vector3.up).normalized;
        if (fwd == Vector3.zero) fwd = Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

        Vector3 dir = (fwd + right * Mathf.Tan(40f * Mathf.Deg2Rad)).normalized;
        dir = (dir + Vector3.up * Mathf.Tan(10f * Mathf.Deg2Rad)).normalized;

        alertMarkerGo.transform.position = cameraAnchor.position + dir * alertDistance;
        Vector3 d = (cameraAnchor.position - alertMarkerGo.transform.position).normalized;
        if (d != Vector3.zero)
            alertMarkerGo.transform.rotation = Quaternion.LookRotation(d, Vector3.up);

        alertActive        = true;
        alertActivatedTime = Time.time;
        alertMarkerGo.SetActive(true);
    }

    private void DismissAlert()
    {
        if (alertMarkerGo != null) alertMarkerGo.SetActive(false);
        alertActive = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetState(string text, Color color)
    {
        if (stateText == null) return;
        stateText.text  = text;
        stateText.color = color;
    }

    private void SetTimer(string text, Color color)
    {
        if (timerText == null) return;
        timerText.text  = text;
        timerText.color = color;
    }

    private static string FormatTime(float s)
    {
        s = Mathf.Max(0f, s);
        return $"{(int)(s / 60f):D2}:{(int)(s % 60f):D2}";
    }

    private void StretchFill(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private Text MakeText(string name, Transform parent,
                          Vector2 anchorMin, Vector2 anchorMax,
                          string defaultText, int fontSize,
                          TextAnchor alignment, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();

        t.text               = defaultText;
        t.fontSize           = fontSize;
        t.alignment          = alignment;
        t.color              = color;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;   // never clip horizontally
        t.verticalOverflow   = VerticalWrapMode.Overflow;     // never clip vertically — CRITICAL for timer

        Font f = japaneseFont;
        if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (f != null) t.font = f;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        return t;
    }
}
