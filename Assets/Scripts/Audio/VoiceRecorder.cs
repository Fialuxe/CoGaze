using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Photon.Voice.Unity;

// Records mic + remote audio to per-condition WAV pairs; independent of PV2 so recording survives voice drops.
public class VoiceRecorder : MonoBehaviour
{
    private const int k_sampleRate         = 16000;
    // Initial buffer reservation per session. Audio is flushed + cleared at each condition boundary
    // (see SaveSession), so a single session never approaches the old 30-minute whole-run size.
    private const int k_recordingCapacity  = k_sampleRate * 60 * 10;

    // Mic-start watchdog tuning.
    private const int   k_micMaxRetries   = 5;     // restart attempts before giving up (and alerting)
    private const float k_micStallTimeout = 3f;    // seconds without a position advance => stalled

    private string _saveDir;
    private string _micDevice;
    private string _wavTimestamp;

    private AudioClip     _micClip;
    private int           _lastMicSample;
    private bool          _isCapturing;
    private List<float>   _localSamples  = new List<float>(k_recordingCapacity);

    private List<float>   _remoteSamples = new List<float>(k_recordingCapacity);
    internal readonly object remoteLock = new object();
    // Actual capture rate of the remote stream (DSP/output rate, ~48kHz) — NOT 16kHz. Used so the
    // remote WAV header matches the data; mismatching it corrupts the remote research channel.
    private int           _remoteSampleRate;

    // Actual capture sample rate (may differ from k_sampleRate on USB devices with restricted caps).
    private int           _localSampleRate = k_sampleRate;

    // Mic health/watchdog state (all touched only on the main thread: StartMic + the coroutines).
    private int           _lastMicPos;
    private float         _lastMicProgressTime;
    private bool          _micConfirmed;
    private bool          _micGaveUp;

    private ExperimentManager2 _experiment;
    private int                _sessionIndex;
    private string             _lastLocalWavPath;

    // Path of the most recently written per-session local WAV (falls back to the first session's
    // name until anything is saved, so a consumer reading it early still gets a sensible path).
    public string LocalWavPath => _lastLocalWavPath ?? (string.IsNullOrEmpty(_saveDir) ? null
        : Path.Combine(_saveDir, $"voice_local_{_wavTimestamp}_s01.wav"));

    public float RecordingSeconds => _localSamples.Count / (float)k_sampleRate;

    public void Initialize(bool isExpert, string _saveDirectory, string preferredDevice = null)
    {
        _saveDir      = _saveDirectory;
        _wavTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // Default remote rate to the current DSP/output rate; refined to the capture component's
        // value in AttachRemoteCapture. Only used when remote samples actually exist.
        _remoteSampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        StartMic(preferredDevice);
        StartCoroutine(CaptureLoop());
        StartCoroutine(MicWatchdog());

        // Flush one WAV per condition (root-cause fix: previously the whole session was buffered in
        // RAM and written only in OnDestroy, so any crash/force-quit lost all audio).
        _experiment = FindAnyObjectByType<ExperimentManager2>();
        if (_experiment != null) _experiment.OnStateChanged += OnExperimentStateChanged;
        else Debug.LogWarning("[VoiceRecorder] ExperimentManager2 not found — per-session save disabled (will still save on destroy).");

        Debug.Log($"[VoiceRecorder] Ready  mic={_micDevice ?? "(default)"}  dir={_saveDir}");
    }

    // Call once the remote Speaker is available (e.g. PhotonVoiceView.SpeakerInUse != null).
    public void AttachRemoteCapture(Speaker speaker)
    {
        if (speaker == null) { Debug.LogWarning("[VoiceRecorder] AttachRemoteCapture: speaker is null."); return; }
        var src = speaker.GetComponent<AudioSource>();
        if (src == null) { Debug.LogWarning("[VoiceRecorder] Speaker has no AudioSource."); return; }
        var cap = src.gameObject.AddComponent<RemoteAudioCapture>();
        cap.Initialize(_remoteSamples, remoteLock);
        // Record the stream's true (DSP/output) capture rate so the remote WAV header is correct.
        if (cap.SampleRate > 0) _remoteSampleRate = cap.SampleRate;
        Debug.Log($"[VoiceRecorder] Remote audio capture attached  rate={_remoteSampleRate}Hz mono.");
    }

    private bool StartMic(string preferred)
    {
        try
        {
            if (Microphone.devices.Length == 0)
            {
                ReportMicFailure("No microphone devices available.");
                _micDevice = null; _micClip = null; _isCapturing = false;
                return false;
            }
            bool ok = !string.IsNullOrEmpty(preferred)
                   && Array.Exists(Microphone.devices, d => d == preferred);
            _micDevice = ok ? preferred : Microphone.devices[0];

            // Query device-supported frequencies; USB audio devices may not support 16kHz
            // and will stall silently if forced to that rate.
            Microphone.GetDeviceCaps(_micDevice, out int minFreq, out int maxFreq);
            int freq = k_sampleRate;
            if (minFreq > 0 || maxFreq > 0)
            {
                if (maxFreq > 0 && freq > maxFreq) freq = maxFreq;
                if (minFreq > 0 && freq < minFreq) freq = minFreq;
            }
            if (freq != k_sampleRate)
                FileLogger.Log("VoiceRecorder", $"Mic '{_micDevice}' caps [{minFreq},{maxFreq}]Hz — using {freq}Hz instead of {k_sampleRate}Hz.");
            _localSampleRate = freq;

            _micClip = Microphone.Start(_micDevice, true, 10, freq);
            if (_micClip == null)
            {
                ReportMicFailure($"Microphone.Start returned null for '{MicName}'.");
                _isCapturing = false;
                return false;
            }
            // Warm-up / stall detection is confirmed asynchronously by MicWatchdog (no busy-wait that
            // would block the main thread). Seed progress tracking so the watchdog has a baseline.
            _lastMicSample        = 0;
            _lastMicPos          = 0;
            _lastMicProgressTime = Time.realtimeSinceStartup;
            _isCapturing          = true;
            return true;
        }
        catch (Exception ex)
        {
            ReportMicFailure($"Mic start failed: {ex.Message}");
            _isCapturing = false;
            return false;
        }
    }

    private string MicName => string.IsNullOrEmpty(_micDevice) ? "(default)" : _micDevice;

    // Persistent + operator-visible alert. Debug.LogError surfaces as a red console/logcat entry;
    // FileLogger gives a durable on-disk record (no-op until FileLogger.Init has run, never throws).
    private void ReportMicFailure(string msg)
    {
        Debug.LogError($"[VoiceRecorder] MIC: {msg}");
        FileLogger.Log("VoiceRecorder", $"MIC: {msg}");
    }

    // Watchdog: a mic that never starts, is unplugged, or silently stops advancing would otherwise
    // leave the ENTIRE local channel silent with no warning. Detect that and restart, with a budget.
    private IEnumerator MicWatchdog()
    {
        var wait = new WaitForSeconds(1f);
        int retries = 0;
        while (true)
        {
            yield return wait;

            bool recording = _isCapturing && _micClip != null && Microphone.IsRecording(_micDevice);
            if (recording)
            {
                int pos = Microphone.GetPosition(_micDevice);
                if (pos != _lastMicPos)
                {
                    // Mic is producing samples — healthy.
                    _lastMicPos          = pos;
                    _lastMicProgressTime = Time.realtimeSinceStartup;
                    retries              = 0;
                    if (!_micConfirmed)
                    {
                        _micConfirmed = true;
                        FileLogger.Log("VoiceRecorder", $"Mic '{MicName}' confirmed producing samples.");
                    }
                    _micGaveUp = false;
                    continue;
                }
            }

            // Not advancing yet — covers BOTH "IsRecording not true yet" (Android mic warm-up can lag
            // Start) and "recording but position stuck". The grace window gates ALL restarts so we never
            // churn-restart a mic that is merely warming up and fire a false silence alarm.
            if (Time.realtimeSinceStartup - _lastMicProgressTime < k_micStallTimeout) continue;

            if (retries >= k_micMaxRetries)
            {
                if (!_micGaveUp)
                {
                    _micGaveUp = true;
                    ReportMicFailure($"Mic '{MicName}' still not producing samples after " +
                                     $"{k_micMaxRetries} restarts — LOCAL AUDIO MAY BE SILENT.");
                }
                continue;
            }

            retries++;
            ReportMicFailure($"Mic '{MicName}' not recording/stalled — restart {retries}/{k_micMaxRetries}.");
            RestartMic();
        }
    }

    // Restart the mic without discarding already-captured _localSamples (only the clip read cursor is
    // reset). All callers are main-thread coroutines, so no locking is needed for the local buffer.
    private void RestartMic()
    {
        try { if (_micClip != null && Microphone.IsRecording(_micDevice)) Microphone.End(_micDevice); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Mic stop before restart failed: {ex.Message}"); }
        _micClip       = null;
        _isCapturing   = false;
        _micConfirmed = false;
        StartMic(_micDevice);
    }

    private IEnumerator CaptureLoop()
    {
        var wait = new WaitForSeconds(0.02f);
        while (true)
        {
            yield return wait;
            if (!_isCapturing || _micClip == null) continue;
            try
            {
                int pos       = Microphone.GetPosition(_micDevice);
                int available = (pos - _lastMicSample + _micClip.samples) % _micClip.samples;
                if (available <= 0) continue;
                available = Mathf.Min(available, 2560); // cap to 8×320 frames per tick
                var buf = new float[available];
                _micClip.GetData(buf, _lastMicSample);
                _lastMicSample = (_lastMicSample + available) % _micClip.samples;
                _localSamples.AddRange(buf);
            }
            catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Capture error: {ex.Message}"); }
        }
    }

    public void SaveSession()
    {
        if (string.IsNullOrEmpty(_saveDir)) return;

        // Snapshot + clear. _localSamples is touched only on the main thread (CaptureLoop coroutine +
        // this call); _remoteSamples is also written from the audio thread — so lock only that one.
        var localSnap = new List<float>(_localSamples);
        _localSamples.Clear();
        List<float> remoteSnap;
        lock (remoteLock) { remoteSnap = new List<float>(_remoteSamples); _remoteSamples.Clear(); }

        if (localSnap.Count == 0 && remoteSnap.Count == 0) return; // nothing buffered this session

        try   { Directory.CreateDirectory(_saveDir); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Cannot create dir: {ex.Message}"); return; }

        _sessionIndex++;
        string tag = $"{_wavTimestamp}_s{_sessionIndex:D2}";
        _lastLocalWavPath = Path.Combine(_saveDir, $"voice_local_{tag}.wav");
        // Local mic is captured at _localSampleRate (usually 16kHz, may be higher for USB devices);
        // remote is captured at the DSP/output rate (~48kHz) — each WAV declares its OWN rate.
        WriteWav(localSnap,  _lastLocalWavPath,                                  _localSampleRate,  1);
        WriteWav(remoteSnap, Path.Combine(_saveDir, $"voice_remote_{tag}.wav"),   _remoteSampleRate, 1);
    }

    private void OnExperimentStateChanged(ExperimentState state)
    {
        // One WAV per condition. Flush at the per-condition NASA-TLX questionnaire (the only
        // Questionnaire-state entry that is exactly one-per-condition — ConditionStart, Alignment and
        // Rest also transition to the Questionnaire state), and at Finished (end of run / SSQ).
        bool perConditionQuestionnaire = state == ExperimentState.Questionnaire
            && _experiment != null && _experiment.CurrentStepType == StepType.Questionnaire;
        if (perConditionQuestionnaire || state == ExperimentState.Finished)
            SaveSession();
    }

    // sampleRate and channels MUST match how the buffer was actually captured, or playback is wrong
    // speed / garbled. byteRate and blockAlign are derived from them so the header stays self-consistent.
    private static void WriteWav(List<float> samples, string path, int sampleRate, int channels)
    {
        if (samples == null || samples.Count == 0)
        { Debug.LogWarning($"[VoiceRecorder] No audio for {path}"); return; }
        if (sampleRate <= 0) { sampleRate = k_sampleRate; }   // defensive: never write a 0Hz header
        if (channels  <= 0) { channels  = 1; }
        try
        {
            const int bitsPerSample  = 16;
            const int bytesPerSample = bitsPerSample / 8;
            int count      = samples.Count;
            int byteData   = count * bytesPerSample;
            int blockAlign = channels * bytesPerSample;
            int byteRate   = sampleRate * blockAlign;
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + byteData);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); bw.Write(16);
            bw.Write((short)1); bw.Write((short)channels);
            bw.Write(sampleRate); bw.Write(byteRate);
            bw.Write((short)blockAlign); bw.Write((short)bitsPerSample);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(byteData);
            foreach (float s in samples)
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
            Debug.Log($"[VoiceRecorder] Saved {path}  ({count / (float)(sampleRate * channels):F1}s @ {sampleRate}Hz x{channels})");
        }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] WAV write failed: {ex.Message}"); }
    }

    private void OnDestroy()
    {
        _isCapturing = false;
        if (_experiment != null) _experiment.OnStateChanged -= OnExperimentStateChanged;
        try { if (_micClip != null) Microphone.End(_micDevice); } catch { }
        try { SaveSession(); } catch { }   // persist whatever remains in the final, unflushed session
    }
}
