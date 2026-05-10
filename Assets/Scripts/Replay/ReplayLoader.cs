using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Networking;

/// <summary>
/// Builds the replay UI programmatically and wires it to ReplayManager.
///
/// Row 1 (top)  : [Folder path InputField] [Open]
/// Row 2        : [◄ Prev] [Trial dropdown] [Next ►]
/// Row 3        : [Play/Pause] [timeline] [Speed] [time]
/// Status bar   : load result / error / audio status
///
/// Opening a folder reads trials.csv and auto-loads the first trial's JSON.
/// Audio (voice_local_*.wav) is loaded asynchronously and played in sync with the timeline.
/// </summary>
public class ReplayLoader : MonoBehaviour
{
    private ReplayManager mgr;

    // Timeline state
    private RectTransform timelineBg;
    private RectTransform timelineFill;
    private bool          isSeeking;

    // UI references
    private Text     playPauseLabel;
    private Text     timeLabel;
    private Text     statusText;
    private Dropdown trialDropdown;

    // Trial list (populated from trials.csv)
    private List<TrialEntry> trials       = new List<TrialEntry>();
    private int              currentTrial = -1;

    // Voice audio
    private AudioSource voiceSource;
    private AudioClip   voiceClip;
    private float       voiceStartSeconds;

    private struct TrialEntry
    {
        public string trialId;
        public string displayLabel;
        public string jsonPath;
    }

    public void Initialize(ReplayManager manager)
    {
        mgr = manager;

        try
        {
            EnsureEventSystem();
            BuildAudioSource();
            BuildUI();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ReplayLoader] UI build failed: {ex.Message}");
            return;
        }

        mgr.OnLoaded     += OnLoaded;
        mgr.OnLoadFailed += OnLoadFailed;
    }

    private void BuildAudioSource()
    {
        var go = new GameObject("ReplayVoice");
        voiceSource             = go.AddComponent<AudioSource>();
        voiceSource.spatialBlend = 0f;
        voiceSource.volume      = 1f;
        voiceSource.playOnAwake = false;
    }

    private IEnumerator LoadWavAsync(string path, string statusPrefix)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            UpdateStatus($"{statusPrefix}  |  No audio");
            yield break;
        }

        string uri = "file:///" + path.Replace('\\', '/');
        using var req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            if (voiceClip != null) Destroy(voiceClip);
            voiceClip        = DownloadHandlerAudioClip.GetContent(req);
            voiceSource.clip = voiceClip;
            UpdateStatus($"{statusPrefix}  |  Audio: {voiceClip.length:F1}s");
        }
        else
        {
            voiceClip        = null;
            voiceSource.clip = null;
            UpdateStatus($"{statusPrefix}  |  Audio load failed: {req.error}");
            Debug.LogWarning($"[ReplayLoader] WAV load failed ({path}): {req.error}");
        }
    }

    private void SyncAudio()
    {
        if (voiceSource == null || voiceClip == null || mgr == null) return;

        float targetTime = Mathf.Clamp(voiceStartSeconds + mgr.CurrentTime, 0f, voiceClip.length);

        if (mgr.IsPlaying)
        {
            voiceSource.pitch = mgr.PlaybackSpeed;
            if (!voiceSource.isPlaying)
            {
                voiceSource.time = targetTime;
                voiceSource.Play();
            }
            else if (Mathf.Abs(voiceSource.time - targetTime) > 0.3f)
            {
                voiceSource.time = targetTime;
            }
        }
        else
        {
            if (voiceSource.isPlaying) voiceSource.Pause();
        }
    }

    private void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;
        var esGo = new GameObject("EventSystem");
        esGo.AddComponent<EventSystem>();
        try { esGo.AddComponent<StandaloneInputModule>(); }
        catch { /* new input system */ }
    }

    private void BuildUI()
    {
        var canvasGo = new GameObject("ReplayCanvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        // Root panel — bottom 24% of screen (3 rows + status fits without cramping)
        var panel = MakePanel(canvasGo.transform, new Vector2(0, 0), new Vector2(1, 0.24f),
                              new Color(0f, 0f, 0f, 0.75f));

        // ── Row 1 (top): folder path + Open ──────────────────────
        string defaultFolder = Path.Combine(Application.persistentDataPath, "logs", "P0");
        var folderField = MakeInputField(panel, new Vector2(0.01f, 0.77f), new Vector2(0.76f, 0.97f),
                                         defaultFolder);

        MakeButton(panel, new Vector2(0.77f, 0.77f), new Vector2(0.99f, 0.97f), "Open",
                   new Color(0.2f, 0.5f, 0.85f),
                   () => OpenFolder(folderField.text.Trim()));

        // ── Row 2: ◄  trial dropdown  ► ──────────────────────────
        MakeButton(panel, new Vector2(0.01f, 0.53f), new Vector2(0.07f, 0.75f), "◄",
                   new Color(0.3f, 0.3f, 0.4f),
                   () => NavigateTrial(-1));

        trialDropdown = BuildTrialDropdown(panel, new Vector2(0.08f, 0.53f), new Vector2(0.92f, 0.75f));

        MakeButton(panel, new Vector2(0.93f, 0.53f), new Vector2(0.99f, 0.75f), "►",
                   new Color(0.3f, 0.3f, 0.4f),
                   () => NavigateTrial(+1));

        // ── Row 3: Play/Pause | timeline | speed | time ──────────
        var playBtn = MakeButton(panel, new Vector2(0.01f, 0.14f), new Vector2(0.09f, 0.50f), "Play",
                                 new Color(0.2f, 0.6f, 0.3f),
                                 () => { if (mgr.IsPlaying) mgr.Pause(); else mgr.Play(); });
        playPauseLabel = playBtn.GetComponentInChildren<Text>();

        BuildTimeline(panel, new Vector2(0.10f, 0.17f), new Vector2(0.74f, 0.50f));

        BuildSpeedDropdown(panel, new Vector2(0.75f, 0.14f), new Vector2(0.89f, 0.50f));

        timeLabel = MakeText(panel, new Vector2(0.90f, 0.14f), new Vector2(0.99f, 0.50f),
                             "0.0 / 0.0", 13, TextAnchor.MiddleCenter);

        // ── Status bar ───────────────────────────────────────────
        statusText = MakeText(panel, new Vector2(0.01f, 0f), new Vector2(0.99f, 0.13f),
                              "Open a P{n} folder to load trials.", 11, TextAnchor.MiddleLeft);
    }

    // ── Folder / trial navigation ────────────────────────────────────────

    public void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            UpdateStatus($"Folder not found: {folder}");
            return;
        }

        string csvPath = Path.Combine(folder, "trials.csv");
        if (!File.Exists(csvPath))
        {
            UpdateStatus($"No trials.csv in: {folder}");
            return;
        }

        trials.Clear();
        try
        {
            string[] lines = File.ReadAllLines(csvPath, Encoding.UTF8);
            // Header: trial_id,participant,condition_index,gaze_mode,noise_level,step_type,step_index,start_ms,end_ms,duration_ms
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                string[] cols = line.Split(',');
                if (cols.Length < 10) continue;

                string trialId  = cols[0].Trim();
                string jsonPath = Path.Combine(folder, $"replay_{trialId}.json");
                if (!File.Exists(jsonPath)) continue;

                string gazeMode = cols[3].Trim();
                string noise    = cols[4].Trim();
                string stepType = cols[5].Trim();
                int.TryParse(cols[6].Trim(),  out int  stepIdx);
                long.TryParse(cols[9].Trim(), out long durMs);

                trials.Add(new TrialEntry
                {
                    trialId      = trialId,
                    jsonPath     = jsonPath,
                    displayLabel = $"#{i} {stepType}-{stepIdx} | {gazeMode} | {noise} | {durMs / 1000f:F0}s"
                });
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"CSV parse error: {ex.Message}");
            return;
        }

        if (trials.Count == 0)
        {
            UpdateStatus("No matching replay JSON files found.");
            return;
        }

        RefreshTrialDropdown();
        UpdateStatus($"{trials.Count} trials loaded from {Path.GetFileName(folder)}.");
    }

    private void RefreshTrialDropdown()
    {
        if (trialDropdown == null) return;
        trialDropdown.onValueChanged.RemoveAllListeners();
        trialDropdown.ClearOptions();

        var opts = new List<string>();
        foreach (var t in trials) opts.Add(t.displayLabel);
        trialDropdown.AddOptions(opts);
        trialDropdown.value = 0;

        trialDropdown.onValueChanged.AddListener(idx =>
        {
            currentTrial = idx;
            LoadTrialAt(idx);
        });

        currentTrial = 0;
        LoadTrialAt(0);
    }

    private void NavigateTrial(int delta)
    {
        if (trials.Count == 0) return;
        int next = Mathf.Clamp(currentTrial + delta, 0, trials.Count - 1);
        if (next == currentTrial) return;
        trialDropdown.value = next; // fires onValueChanged → LoadTrialAt
    }

    private void LoadTrialAt(int idx)
    {
        if (idx < 0 || idx >= trials.Count) return;
        try { mgr.Load(trials[idx].jsonPath); }
        catch (Exception ex)
        {
            Debug.LogError($"[ReplayLoader] Load error: {ex.Message}");
            UpdateStatus($"Load error: {ex.Message}");
        }
    }

    // ── Timeline ─────────────────────────────────────────────────────────

    private void BuildTimeline(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var bgGo = new GameObject("TimelineBg");
        bgGo.transform.SetParent(parent, false);
        timelineBg            = bgGo.AddComponent<RectTransform>();
        timelineBg.anchorMin  = anchorMin;
        timelineBg.anchorMax  = anchorMax;
        timelineBg.offsetMin  = timelineBg.offsetMax = Vector2.zero;
        bgGo.AddComponent<Image>().color = new Color(1, 1, 1, 0.15f);

        var fillGo = new GameObject("TimelineFill");
        fillGo.transform.SetParent(bgGo.transform, false);
        timelineFill            = fillGo.AddComponent<RectTransform>();
        timelineFill.anchorMin  = Vector2.zero;
        timelineFill.anchorMax  = new Vector2(0f, 1f);
        timelineFill.offsetMin  = timelineFill.offsetMax = Vector2.zero;
        fillGo.AddComponent<Image>().color = new Color(0.3f, 0.7f, 1f, 0.85f);

        var trigger = bgGo.AddComponent<EventTrigger>();
        AddTrigger(trigger, EventTriggerType.PointerDown, e =>
        {
            isSeeking = true;
            SeekFromPointer(e as PointerEventData);
        });
        AddTrigger(trigger, EventTriggerType.Drag, e =>
        {
            if (isSeeking) SeekFromPointer(e as PointerEventData);
        });
        AddTrigger(trigger, EventTriggerType.PointerUp, _ => isSeeking = false);
    }

    private void SeekFromPointer(PointerEventData ev)
    {
        if (ev == null || mgr == null || mgr.TotalDuration <= 0f) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                timelineBg, ev.position, ev.pressEventCamera, out Vector2 lp)) return;
        float t = Mathf.InverseLerp(timelineBg.rect.xMin, timelineBg.rect.xMax, lp.x);
        try { mgr.SeekTo(t * mgr.TotalDuration); }
        catch (Exception ex) { Debug.LogWarning($"[ReplayLoader] Seek error: {ex.Message}"); }
    }

    private Dropdown BuildTrialDropdown(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject("TrialDropdown");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.3f, 1f);

        var caption = MakeChildText(go.transform, "Label", "— Open a folder first —", 12);
        var dd      = go.AddComponent<Dropdown>();
        dd.captionText = caption;
        dd.ClearOptions();
        return dd;
    }

    private void BuildSpeedDropdown(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject("SpeedDropdown");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.35f, 1f);

        var label = MakeChildText(go.transform, "Caption", "1×", 13);
        var dd    = go.AddComponent<Dropdown>();
        dd.captionText = label;
        dd.ClearOptions();
        dd.AddOptions(new List<string> { "0.5×", "1×", "2×", "4×" });
        dd.value = 1;
        dd.onValueChanged.AddListener(idx =>
        {
            float[] speeds = { 0.5f, 1f, 2f, 4f };
            if (mgr != null) mgr.PlaybackSpeed = speeds[Mathf.Clamp(idx, 0, speeds.Length - 1)];
        });
    }

    // ── Unity loop ───────────────────────────────────────────────────────

    private void Update()
    {
        if (mgr == null) return;

        if (playPauseLabel != null)
            playPauseLabel.text = mgr.IsPlaying ? "Pause" : "Play";

        if (timeLabel != null)
            timeLabel.text = $"{mgr.CurrentTime:F1} / {mgr.TotalDuration:F1}";

        if (timelineFill != null && mgr.TotalDuration > 0f && !isSeeking)
        {
            float t = Mathf.Clamp01(mgr.CurrentTime / mgr.TotalDuration);
            timelineFill.anchorMax = new Vector2(t, 1f);
        }

        SyncAudio();
    }

    // ── Callbacks ────────────────────────────────────────────────────────

    private void OnLoaded(ReplayData data)
    {
        int    frames     = data.frames?.Count ?? 0;
        string baseStatus =
            $"Loaded: {data.meta?.trialId}  |  {data.meta?.gazeMode}  |  {data.meta?.noiseLevel}  |  " +
            $"{frames} frames  ({data.meta?.stepType})";

        UpdateStatus(baseStatus);

        // Reset audio for new trial
        voiceSource.Stop();
        if (voiceClip != null) { Destroy(voiceClip); voiceClip = null; }
        voiceSource.clip  = null;
        voiceStartSeconds = data.meta?.voiceStartSeconds ?? 0f;

        string wavPath = data.meta?.voiceWavPath;
        if (!string.IsNullOrEmpty(wavPath))
            StartCoroutine(LoadWavAsync(wavPath, baseStatus));
    }

    private void OnLoadFailed(string msg) => UpdateStatus($"Error: {msg}");

    private void UpdateStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }

    private void OnDestroy()
    {
        if (mgr == null) return;
        mgr.OnLoaded     -= OnLoaded;
        mgr.OnLoadFailed -= OnLoadFailed;
    }

    // ── UI factory helpers ───────────────────────────────────────────────

    private RectTransform MakePanel(Transform parent, Vector2 ancMin, Vector2 ancMax, Color color)
    {
        var go = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = color;
        return rt;
    }

    private InputField MakeInputField(RectTransform parent, Vector2 ancMin, Vector2 ancMax,
                                      string placeholder)
    {
        var go = new GameObject("InputField");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = new Color(1, 1, 1, 0.12f);

        var textComp = MakeChildText(go.transform, "Text",        "",          13);
        var phComp   = MakeChildText(go.transform, "Placeholder", placeholder, 13);
        phComp.color     = new Color(1, 1, 1, 0.4f);
        phComp.fontStyle = FontStyle.Italic;

        var field           = go.AddComponent<InputField>();
        field.textComponent = textComp;
        field.placeholder   = phComp;
        return field;
    }

    private Button MakeButton(RectTransform parent, Vector2 ancMin, Vector2 ancMax,
                               string label, Color bgColor, Action onClick)
    {
        var go = new GameObject($"Btn_{label}");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.color = bgColor;

        MakeChildText(go.transform, "Label", label, 14, TextAnchor.MiddleCenter);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() =>
        {
            try { onClick?.Invoke(); }
            catch (Exception ex) { Debug.LogError($"[ReplayLoader] Button '{label}' error: {ex.Message}"); }
        });
        return btn;
    }

    private Text MakeText(RectTransform parent, Vector2 ancMin, Vector2 ancMax,
                          string content, int fontSize, TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = ancMin; rt.anchorMax = ancMax;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var t = go.AddComponent<Text>();
        t.text      = content;
        t.fontSize  = fontSize;
        t.color     = Color.white;
        t.alignment = alignment;
        t.font      = GetFont();
        return t;
    }

    private Text MakeChildText(Transform parent, string name, string content, int fontSize,
                                TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(4, 0); rt.offsetMax = new Vector2(-4, 0);
        var t = go.AddComponent<Text>();
        t.text      = content;
        t.fontSize  = fontSize;
        t.color     = Color.white;
        t.alignment = alignment;
        t.font      = GetFont();
        return t;
    }

    private static Font GetFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return f;
    }

    private static void AddTrigger(EventTrigger trigger, EventTriggerType type,
                                    UnityEngine.Events.UnityAction<BaseEventData> callback)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(callback);
        trigger.triggers.Add(entry);
    }
}
