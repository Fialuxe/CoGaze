using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Networking;

// Builds replay UI programmatically (folder picker, trial dropdown, timeline, speed), wired to ReplayManager.
public class ReplayLoader : MonoBehaviour
{
    private ReplayManager _mgr;

    // Timeline state
    private RectTransform _timelineBg;
    private RectTransform _timelineFill;
    private bool          _isSeeking;

    // UI references
    private Text     _playPauseLabel;
    private Text     _timeLabel;
    private Text     _statusText;
    private Dropdown _trialDropdown;

    // Trial list (populated from _trials.csv)
    private List<TrialEntry> _trials       = new List<TrialEntry>();
    private int              _currentTrial = -1;

    // Voice audio
    private AudioSource _voiceSource;
    private AudioClip   _voiceClip;
    private float       _voiceStartSeconds;
    // Generation token: bumped each time a WAV load starts. A slower, older load that finishes
    // after a newer trial was selected sees a stale token and discards its result, so it can
    // never clobber the newer trial's audio/status.
    private int         _wavLoadGeneration;

    private struct TrialEntry
    {
        public string trialId;
        public string displayLabel;
        public string jsonPath;
    }

    public void Initialize(ReplayManager manager)
    {
        _mgr = manager;

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

        _mgr.OnLoaded     += OnLoaded;
        _mgr.OnLoadFailed += OnLoadFailed;
    }

    private void BuildAudioSource()
    {
        var go = new GameObject("ReplayVoice");
        _voiceSource             = go.AddComponent<AudioSource>();
        _voiceSource.spatialBlend = 0f;
        _voiceSource.volume      = 1f;
        _voiceSource.playOnAwake = false;
    }

    private IEnumerator LoadWavAsync(string path, string statusPrefix)
    {
        // Claim a new generation up front; any in-flight older load is now superseded.
        int gen = ++_wavLoadGeneration;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            UpdateStatus($"{statusPrefix}  |  No audio");
            yield break;
        }

        string uri = "file:///" + path.Replace('\\', '/');
        using var req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV);
        yield return req.SendWebRequest();

        // A newer trial load started while this WAV was downloading — discard the stale result
        // before touching _voiceClip / _voiceSource.clip / status.
        if (gen != _wavLoadGeneration) yield break;

        if (req.result == UnityWebRequest.Result.Success)
        {
            if (_voiceClip != null) Destroy(_voiceClip);
            _voiceClip        = DownloadHandlerAudioClip.GetContent(req);
            _voiceSource.clip = _voiceClip;
            UpdateStatus($"{statusPrefix}  |  Audio: {_voiceClip.length:F1}s");
        }
        else
        {
            _voiceClip        = null;
            _voiceSource.clip = null;
            UpdateStatus($"{statusPrefix}  |  Audio load failed: {req.error}");
            Debug.LogWarning($"[ReplayLoader] WAV load failed ({path}): {req.error}");
        }
    }

    private void SyncAudio()
    {
        if (_voiceSource == null || _voiceClip == null || _mgr == null) return;

        float targetTime = Mathf.Clamp(_voiceStartSeconds + _mgr.CurrentTime, 0f, _voiceClip.length);

        if (_mgr.IsPlaying)
        {
            _voiceSource.pitch = _mgr.PlaybackSpeed;
            if (!_voiceSource.isPlaying)
            {
                _voiceSource.time = targetTime;
                _voiceSource.Play();
            }
            else if (Mathf.Abs(_voiceSource.time - targetTime) > 0.3f)
            {
                _voiceSource.time = targetTime;
            }
        }
        else
        {
            if (_voiceSource.isPlaying) _voiceSource.Pause();
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

        _trialDropdown = BuildTrialDropdown(panel, new Vector2(0.08f, 0.53f), new Vector2(0.92f, 0.75f));

        MakeButton(panel, new Vector2(0.93f, 0.53f), new Vector2(0.99f, 0.75f), "►",
                   new Color(0.3f, 0.3f, 0.4f),
                   () => NavigateTrial(+1));

        // ── Row 3: Play/Pause | timeline | speed | time ──────────
        var playBtn = MakeButton(panel, new Vector2(0.01f, 0.14f), new Vector2(0.09f, 0.50f), "Play",
                                 new Color(0.2f, 0.6f, 0.3f),
                                 () => { if (_mgr.IsPlaying) _mgr.Pause(); else _mgr.Play(); });
        _playPauseLabel = playBtn.GetComponentInChildren<Text>();

        BuildTimeline(panel, new Vector2(0.10f, 0.17f), new Vector2(0.74f, 0.50f));

        BuildSpeedDropdown(panel, new Vector2(0.75f, 0.14f), new Vector2(0.89f, 0.50f));

        _timeLabel = MakeText(panel, new Vector2(0.90f, 0.14f), new Vector2(0.99f, 0.50f),
                             "0.0 / 0.0", 13, TextAnchor.MiddleCenter);

        // ── Status bar ───────────────────────────────────────────
        _statusText = MakeText(panel, new Vector2(0.01f, 0f), new Vector2(0.99f, 0.13f),
                              "Open a P{n} folder to load _trials.", 11, TextAnchor.MiddleLeft);
    }

    // ── Folder / trial navigation ────────────────────────────────────────

    public void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            UpdateStatus($"Folder not found: {folder}");
            return;
        }

        string csvPath = Path.Combine(folder, "_trials.csv");
        if (!File.Exists(csvPath))
        {
            UpdateStatus($"No _trials.csv in: {folder}");
            return;
        }

        _trials.Clear();
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

                _trials.Add(new TrialEntry
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

        if (_trials.Count == 0)
        {
            UpdateStatus("No matching replay JSON files found.");
            return;
        }

        RefreshTrialDropdown();
        UpdateStatus($"{_trials.Count} _trials loaded from {Path.GetFileName(folder)}.");
    }

    private void RefreshTrialDropdown()
    {
        if (_trialDropdown == null) return;
        _trialDropdown.onValueChanged.RemoveAllListeners();
        _trialDropdown.ClearOptions();

        var opts = new List<string>();
        foreach (var t in _trials) opts.Add(t.displayLabel);
        _trialDropdown.AddOptions(opts);
        _trialDropdown.value = 0;

        _trialDropdown.onValueChanged.AddListener(idx =>
        {
            _currentTrial = idx;
            LoadTrialAt(idx);
        });

        _currentTrial = 0;
        LoadTrialAt(0);
    }

    private void NavigateTrial(int delta)
    {
        if (_trials.Count == 0) return;
        int next = Mathf.Clamp(_currentTrial + delta, 0, _trials.Count - 1);
        if (next == _currentTrial) return;
        _trialDropdown.value = next; // fires onValueChanged → LoadTrialAt
    }

    private void LoadTrialAt(int idx)
    {
        if (idx < 0 || idx >= _trials.Count) return;
        try { _mgr.Load(_trials[idx].jsonPath); }
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
        _timelineBg            = bgGo.AddComponent<RectTransform>();
        _timelineBg.anchorMin  = anchorMin;
        _timelineBg.anchorMax  = anchorMax;
        _timelineBg.offsetMin  = _timelineBg.offsetMax = Vector2.zero;
        bgGo.AddComponent<Image>().color = new Color(1, 1, 1, 0.15f);

        var fillGo = new GameObject("TimelineFill");
        fillGo.transform.SetParent(bgGo.transform, false);
        _timelineFill            = fillGo.AddComponent<RectTransform>();
        _timelineFill.anchorMin  = Vector2.zero;
        _timelineFill.anchorMax  = new Vector2(0f, 1f);
        _timelineFill.offsetMin  = _timelineFill.offsetMax = Vector2.zero;
        fillGo.AddComponent<Image>().color = new Color(0.3f, 0.7f, 1f, 0.85f);

        var trigger = bgGo.AddComponent<EventTrigger>();
        AddTrigger(trigger, EventTriggerType.PointerDown, e =>
        {
            _isSeeking = true;
            SeekFromPointer(e as PointerEventData);
        });
        AddTrigger(trigger, EventTriggerType.Drag, e =>
        {
            if (_isSeeking) SeekFromPointer(e as PointerEventData);
        });
        AddTrigger(trigger, EventTriggerType.PointerUp, _ => _isSeeking = false);
    }

    private void SeekFromPointer(PointerEventData ev)
    {
        if (ev == null || _mgr == null || _mgr.TotalDuration <= 0f) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _timelineBg, ev.position, ev.pressEventCamera, out Vector2 lp)) return;
        float t = Mathf.InverseLerp(_timelineBg.rect.xMin, _timelineBg.rect.xMax, lp.x);
        try { _mgr.SeekTo(t * _mgr.TotalDuration); }
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
            if (_mgr != null) _mgr.PlaybackSpeed = speeds[Mathf.Clamp(idx, 0, speeds.Length - 1)];
        });
    }

    // ── Unity loop ───────────────────────────────────────────────────────

    private void Update()
    {
        if (_mgr == null) return;

        if (_playPauseLabel != null)
            _playPauseLabel.text = _mgr.IsPlaying ? "Pause" : "Play";

        if (_timeLabel != null)
            _timeLabel.text = $"{_mgr.CurrentTime:F1} / {_mgr.TotalDuration:F1}";

        if (_timelineFill != null && _mgr.TotalDuration > 0f && !_isSeeking)
        {
            float t = Mathf.Clamp01(_mgr.CurrentTime / _mgr.TotalDuration);
            _timelineFill.anchorMax = new Vector2(t, 1f);
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
        _voiceSource.Stop();
        if (_voiceClip != null) { Destroy(_voiceClip); _voiceClip = null; }
        _voiceSource.clip  = null;
        _voiceStartSeconds = data.meta?.voiceStartSeconds ?? 0f;

        string wavPath = data.meta?.voiceWavPath;
        if (!string.IsNullOrEmpty(wavPath))
            StartCoroutine(LoadWavAsync(wavPath, baseStatus));
    }

    private void OnLoadFailed(string msg) => UpdateStatus($"Error: {msg}");

    private void UpdateStatus(string msg)
    {
        if (_statusText != null) _statusText.text = msg;
    }

    private void OnDestroy()
    {
        if (_mgr == null) return;
        _mgr.OnLoaded     -= OnLoaded;
        _mgr.OnLoadFailed -= OnLoadFailed;
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
