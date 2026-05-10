using System;
using System.IO;
using System.Text;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Loads a replay JSON file and drives frame-by-frame playback.
/// Added by ReplayBootstrapper. All other replay components subscribe to its events.
/// </summary>
public class ReplayManager : MonoBehaviour
{
    // ── Events ─────────────────────────────────────────────────────────
    public event Action<ReplayData>                 OnLoaded;
    public event Action<string>                     OnLoadFailed;
    public event Action<ReplayFrameData, int>       OnFrameChanged;  // frame, frameIndex

    // ── Public state ───────────────────────────────────────────────────
    public bool       IsPlaying    { get; private set; }
    public float      TotalDuration{ get; private set; }
    public float      CurrentTime  { get; private set; }
    public ReplayData CurrentData  { get; private set; }

    public float PlaybackSpeed
    {
        get => _speed;
        set => _speed = Mathf.Clamp(value, 0.1f, 4f);
    }

    // ── Internal ───────────────────────────────────────────────────────
    private float _speed = 1f;
    private int   _frameIndex = -1;

    // ── API ────────────────────────────────────────────────────────────

    public void Load(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            string msg = "File path is empty.";
            Debug.LogError($"[ReplayManager] {msg}");
            OnLoadFailed?.Invoke(msg);
            return;
        }

        try
        {
            if (!File.Exists(jsonPath))
            {
                string msg = $"File not found: {jsonPath}";
                Debug.LogError($"[ReplayManager] {msg}");
                OnLoadFailed?.Invoke(msg);
                return;
            }

            string     json = File.ReadAllText(jsonPath, Encoding.UTF8);
            ReplayData data = JsonConvert.DeserializeObject<ReplayData>(json);

            if (data == null)
            {
                string msg = "JSON deserialized to null — check file format.";
                Debug.LogError($"[ReplayManager] {msg}");
                OnLoadFailed?.Invoke(msg);
                return;
            }

            if (data.frames == null || data.frames.Count == 0)
            {
                string msg = "Replay file contains no frames.";
                Debug.LogError($"[ReplayManager] {msg}");
                OnLoadFailed?.Invoke(msg);
                return;
            }

            CurrentData   = data;
            TotalDuration = data.frames[data.frames.Count - 1].t;
            CurrentTime   = 0f;
            _frameIndex   = 0;
            IsPlaying     = false;

            Debug.Log($"[ReplayManager] Loaded {data.frames.Count} frames, {TotalDuration:F1}s — {data.meta?.gazeMode}/{data.meta?.noiseLevel}");

            try { OnLoaded?.Invoke(data); }
            catch (Exception ex) { Debug.LogWarning($"[ReplayManager] OnLoaded handler error: {ex.Message}"); }

            // Deliver first frame immediately
            FireFrame(0);
        }
        catch (Exception ex)
        {
            string msg = $"Load error: {ex.Message}";
            Debug.LogError($"[ReplayManager] {msg}");
            OnLoadFailed?.Invoke(msg);
        }
    }

    public void Play()  => IsPlaying = true;
    public void Pause() => IsPlaying = false;

    public void SeekTo(float time)
    {
        if (CurrentData == null) return;
        CurrentTime = Mathf.Clamp(time, 0f, TotalDuration);
        UpdateFrame();
    }

    // ── Unity loop ─────────────────────────────────────────────────────

    private void Update()
    {
        if (!IsPlaying || CurrentData == null) return;

        CurrentTime += Time.deltaTime * _speed;

        if (CurrentTime >= TotalDuration)
        {
            CurrentTime = TotalDuration;
            IsPlaying   = false;
        }

        UpdateFrame();
    }

    // ── Internals ──────────────────────────────────────────────────────

    private void UpdateFrame()
    {
        if (CurrentData?.frames == null || CurrentData.frames.Count == 0) return;

        int idx = FindFrameIndex(CurrentTime);
        if (idx != _frameIndex)
        {
            _frameIndex = idx;
            FireFrame(idx);
        }
    }

    private int FindFrameIndex(float time)
    {
        var frames = CurrentData.frames;
        int lo = 0, hi = frames.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (frames[mid].t <= time) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private void FireFrame(int idx)
    {
        try
        {
            OnFrameChanged?.Invoke(CurrentData.frames[idx], idx);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ReplayManager] OnFrameChanged handler error at frame {idx}: {ex.Message}");
        }
    }
}
