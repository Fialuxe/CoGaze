using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Photon.Voice.Unity;

/// <summary>
/// Records local mic and remote audio to WAV, independent of Photon Voice's Recorder.
/// Uses a separate Microphone.Start() so recording continues even if PV2 drops.
/// Remote audio is captured via RemoteAudioCapture attached to the Speaker's AudioSource.
/// Call AttachRemoteCapture() after the remote player's Speaker is confirmed active.
/// </summary>
public class VoiceRecorder : MonoBehaviour
{
    private const int SAMPLE_RATE         = 16000;
    // Initial buffer reservation per session. Audio is flushed + cleared at each condition boundary
    // (see SaveSession), so a single session never approaches the old 30-minute whole-run size.
    private const int RECORDING_CAPACITY  = SAMPLE_RATE * 60 * 10;

    // Mic-start watchdog tuning.
    private const int   MIC_MAX_RETRIES   = 5;     // restart attempts before giving up (and alerting)
    private const float MIC_STALL_TIMEOUT = 3f;    // seconds without a position advance => stalled

    private string saveDir;
    private string micDevice;
    private string wavTimestamp;

    private AudioClip     micClip;
    private int           lastMicSample;
    private bool          isCapturing;
    private List<float>   localSamples  = new List<float>(RECORDING_CAPACITY);

    private List<float>   remoteSamples = new List<float>(RECORDING_CAPACITY);
    internal readonly object remoteLock = new object();
    // Actual capture rate of the remote stream (DSP/output rate, ~48kHz) — NOT 16kHz. Used so the
    // remote WAV header matches the data; mismatching it corrupts the remote research channel.
    private int           _remoteSampleRate;

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
    public string LocalWavPath => _lastLocalWavPath ?? (string.IsNullOrEmpty(saveDir) ? null
        : Path.Combine(saveDir, $"voice_local_{wavTimestamp}_s01.wav"));

    public float RecordingSeconds => localSamples.Count / (float)SAMPLE_RATE;

    public void Initialize(bool isExpert, string saveDirectory, string preferredDevice = null)
    {
        saveDir      = saveDirectory;
        wavTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
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

        Debug.Log($"[VoiceRecorder] Ready  mic={micDevice ?? "(default)"}  dir={saveDir}");
    }

    // Call once the remote Speaker is available (e.g. PhotonVoiceView.SpeakerInUse != null).
    public void AttachRemoteCapture(Speaker speaker)
    {
        if (speaker == null) { Debug.LogWarning("[VoiceRecorder] AttachRemoteCapture: speaker is null."); return; }
        var src = speaker.GetComponent<AudioSource>();
        if (src == null) { Debug.LogWarning("[VoiceRecorder] Speaker has no AudioSource."); return; }
        var cap = src.gameObject.AddComponent<RemoteAudioCapture>();
        cap.Initialize(remoteSamples, remoteLock);
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
                micDevice = null; micClip = null; isCapturing = false;
                return false;
            }
            bool ok = !string.IsNullOrEmpty(preferred)
                   && Array.Exists(Microphone.devices, d => d == preferred);
            micDevice = ok ? preferred : Microphone.devices[0];
            micClip = Microphone.Start(micDevice, true, 10, SAMPLE_RATE);
            if (micClip == null)
            {
                ReportMicFailure($"Microphone.Start returned null for '{MicName}'.");
                isCapturing = false;
                return false;
            }
            // Warm-up / stall detection is confirmed asynchronously by MicWatchdog (no busy-wait that
            // would block the main thread). Seed progress tracking so the watchdog has a baseline.
            lastMicSample        = 0;
            _lastMicPos          = 0;
            _lastMicProgressTime = Time.realtimeSinceStartup;
            isCapturing          = true;
            return true;
        }
        catch (Exception ex)
        {
            ReportMicFailure($"Mic start failed: {ex.Message}");
            isCapturing = false;
            return false;
        }
    }

    private string MicName => string.IsNullOrEmpty(micDevice) ? "(default)" : micDevice;

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

            bool recording = isCapturing && micClip != null && Microphone.IsRecording(micDevice);
            if (recording)
            {
                int pos = Microphone.GetPosition(micDevice);
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
            if (Time.realtimeSinceStartup - _lastMicProgressTime < MIC_STALL_TIMEOUT) continue;

            if (retries >= MIC_MAX_RETRIES)
            {
                if (!_micGaveUp)
                {
                    _micGaveUp = true;
                    ReportMicFailure($"Mic '{MicName}' still not producing samples after " +
                                     $"{MIC_MAX_RETRIES} restarts — LOCAL AUDIO MAY BE SILENT.");
                }
                continue;
            }

            retries++;
            ReportMicFailure($"Mic '{MicName}' not recording/stalled — restart {retries}/{MIC_MAX_RETRIES}.");
            RestartMic();
        }
    }

    // Restart the mic without discarding already-captured localSamples (only the clip read cursor is
    // reset). All callers are main-thread coroutines, so no locking is needed for the local buffer.
    private void RestartMic()
    {
        try { if (micClip != null && Microphone.IsRecording(micDevice)) Microphone.End(micDevice); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Mic stop before restart failed: {ex.Message}"); }
        micClip       = null;
        isCapturing   = false;
        _micConfirmed = false;
        StartMic(micDevice);
    }

    private IEnumerator CaptureLoop()
    {
        var wait = new WaitForSeconds(0.02f);
        while (true)
        {
            yield return wait;
            if (!isCapturing || micClip == null) continue;
            try
            {
                int pos       = Microphone.GetPosition(micDevice);
                int available = (pos - lastMicSample + micClip.samples) % micClip.samples;
                if (available <= 0) continue;
                available = Mathf.Min(available, 2560); // cap to 8×320 frames per tick
                var buf = new float[available];
                micClip.GetData(buf, lastMicSample);
                lastMicSample = (lastMicSample + available) % micClip.samples;
                localSamples.AddRange(buf);
            }
            catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Capture error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Save the audio buffered since the last call to a numbered per-session WAV pair, then clear the
    /// buffers. Called at each condition boundary (Questionnaire) and on Finished/destroy, so a crash
    /// or force-quit loses at most the current session, and RAM does not grow across a long run.
    /// </summary>
    public void SaveSession()
    {
        if (string.IsNullOrEmpty(saveDir)) return;

        // Snapshot + clear. localSamples is touched only on the main thread (CaptureLoop coroutine +
        // this call); remoteSamples is also written from the audio thread — so lock only that one.
        var localSnap = new List<float>(localSamples);
        localSamples.Clear();
        List<float> remoteSnap;
        lock (remoteLock) { remoteSnap = new List<float>(remoteSamples); remoteSamples.Clear(); }

        if (localSnap.Count == 0 && remoteSnap.Count == 0) return; // nothing buffered this session

        try   { Directory.CreateDirectory(saveDir); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Cannot create dir: {ex.Message}"); return; }

        _sessionIndex++;
        string tag = $"{wavTimestamp}_s{_sessionIndex:D2}";
        _lastLocalWavPath = Path.Combine(saveDir, $"voice_local_{tag}.wav");
        // Local mic is captured at SAMPLE_RATE (16kHz) mono; remote is captured at the DSP/output rate
        // (~48kHz) and mono-downmixed by RemoteAudioCapture — each WAV must declare its OWN rate.
        WriteWav(localSnap,  _lastLocalWavPath,                                  SAMPLE_RATE,       1);
        WriteWav(remoteSnap, Path.Combine(saveDir, $"voice_remote_{tag}.wav"),   _remoteSampleRate, 1);
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
        if (sampleRate <= 0) { sampleRate = SAMPLE_RATE; }   // defensive: never write a 0Hz header
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
        isCapturing = false;
        if (_experiment != null) _experiment.OnStateChanged -= OnExperimentStateChanged;
        try { if (micClip != null) Microphone.End(micDevice); } catch { }
        try { SaveSession(); } catch { }   // persist whatever remains in the final, unflushed session
    }
}
