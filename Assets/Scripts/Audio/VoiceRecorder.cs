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
    private const int RECORDING_CAPACITY  = SAMPLE_RATE * 60 * 30;

    private string saveDir;
    private string micDevice;
    private string wavTimestamp;

    private AudioClip     micClip;
    private int           lastMicSample;
    private bool          isCapturing;
    private List<float>   localSamples  = new List<float>(RECORDING_CAPACITY);

    private List<float>   remoteSamples = new List<float>(RECORDING_CAPACITY);
    internal readonly object remoteLock = new object();

    public string LocalWavPath => string.IsNullOrEmpty(saveDir) ? null
        : Path.Combine(saveDir, $"voice_local_{wavTimestamp}.wav");

    public float RecordingSeconds => localSamples.Count / (float)SAMPLE_RATE;

    public void Initialize(bool isExpert, string saveDirectory, string preferredDevice = null)
    {
        saveDir      = saveDirectory;
        wavTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        StartMic(preferredDevice);
        StartCoroutine(CaptureLoop());
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
        Debug.Log("[VoiceRecorder] Remote audio capture attached.");
    }

    private void StartMic(string preferred)
    {
        try
        {
            bool ok = !string.IsNullOrEmpty(preferred)
                   && Array.Exists(Microphone.devices, d => d == preferred);
            micDevice = ok ? preferred
                : Microphone.devices.Length > 0 ? Microphone.devices[0] : "";
            micClip = Microphone.Start(micDevice, true, 10, SAMPLE_RATE);
            float t = 0f;
            while (Microphone.GetPosition(micDevice) <= 0 && t < 1f) t += 0.02f;
            isCapturing = true;
        }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Mic start failed: {ex.Message}"); }
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

    public void SaveRecordings()
    {
        if (string.IsNullOrEmpty(saveDir)) return;
        try   { Directory.CreateDirectory(saveDir); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] Cannot create dir: {ex.Message}"); return; }

        WriteWav(localSamples, Path.Combine(saveDir, $"voice_local_{wavTimestamp}.wav"));

        List<float> snap;
        lock (remoteLock) snap = new List<float>(remoteSamples);
        WriteWav(snap, Path.Combine(saveDir, $"voice_remote_{wavTimestamp}.wav"));
    }

    private static void WriteWav(List<float> samples, string path)
    {
        if (samples == null || samples.Count == 0)
        { Debug.LogWarning($"[VoiceRecorder] No audio for {path}"); return; }
        try
        {
            int count    = samples.Count;
            int byteData = count * 2;
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + byteData);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); bw.Write(16);
            bw.Write((short)1); bw.Write((short)1);
            bw.Write(SAMPLE_RATE); bw.Write(SAMPLE_RATE * 2);
            bw.Write((short)2); bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(byteData);
            foreach (float s in samples)
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
            Debug.Log($"[VoiceRecorder] Saved {path}  ({count / SAMPLE_RATE:F1}s)");
        }
        catch (Exception ex) { Debug.LogWarning($"[VoiceRecorder] WAV write failed: {ex.Message}"); }
    }

    private void OnDestroy()
    {
        isCapturing = false;
        try { if (micClip != null) Microphone.End(micDevice); } catch { }
        try { SaveRecordings(); } catch { }
    }
}
