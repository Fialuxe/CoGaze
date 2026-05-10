using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Concentus.Enums;
using Concentus.Structs;

/// <summary>
/// Two-way voice communication between Expert (PC) and Worker (Quest).
///
/// Transport : UDP (UdpAudioTransport) — eliminates TCP head-of-line blocking
///             that caused audible dropouts when WiFi retransmitted a packet.
/// Codec     : Opus via Concentus (pure C#).
///             - SILK wideband mode, 16 kbps VBR — clear voice at minimal bandwidth.
///             - Inband FEC: each packet embeds a backup of the previous frame.
///               Single-packet loss is fully recovered without audible artifact.
///             - Built-in PLC (Packet Loss Concealment) for multi-frame gaps.
/// Spatial   : Worker hears Expert via 3D AudioSource positioned at the Expert's
///             PostureHandler. Starts as 2D until the PostureHandler is located
///             (avoids HRTF attenuating the source at world origin on Quest).
/// Recording : Raw PCM saved as WAV for both local and remote channels.
///
/// Added via AddComponent in RemoteExpertSetup / LocalWorkerSetup.
/// Requires Concentus.dll in Assets/Plugins (NuGet: Concentus).
/// NOTE (Quest): RECORD_AUDIO permission must be in AndroidManifest.xml.
/// </summary>
public class VoiceCommunicator : MonoBehaviour
{
    private const int SAMPLE_RATE   = 16000;
    private const int CHUNK_FRAMES  = 320;   // 20 ms per packet at 16 kHz
    private const int TARGET_BUFFER = 1920;  // 120 ms jitter buffer (wider than TCP to absorb UDP jitter)
    private const int MAX_BUFFER    = 6400;  // 400 ms — hard reset threshold

    // Opus tuning
    private const int OPUS_BITRATE      = 16000; // 16 kbps — sufficient for clear wideband voice
    private const int OPUS_LOSS_PERCENT = 10;    // tune FEC aggressiveness to expected WiFi loss rate

    private bool   isExpert;
    private string saveDir;
    private string _preferredDevice;

    // Microphone
    private AudioClip micClip;
    private string    micDevice;
    private int       lastMicSample;
    private float[]   micChunk = new float[CHUNK_FRAMES];

    // Playback (streaming AudioClip → PCM callback, called on audio thread)
    private AudioSource  remoteSource;
    private float[]      ring  = new float[SAMPLE_RATE * 5]; // 5 s ring buffer
    private int          writePos;
    private int          readPos;
    private bool         _playbackStarted;
    private readonly object ringLock = new object();

    // UDP audio transport
    private UdpAudioTransport audioTransport;

    // Opus codec — encoder on send path, decoder on receive path.
    // Pre-allocated scratch buffers avoid per-packet heap allocation.
    private OpusEncoder _opusEncoder;
    private OpusDecoder _opusDecoder;
    private readonly byte[]  _encodeBuf      = new byte[1275]; // max Opus frame (RFC 6716)
    private readonly short[] _pcmShortBuf    = new short[CHUNK_FRAMES]; // float→short for encoder
    private readonly short[] _decodeBuf      = new short[CHUNK_FRAMES]; // decoder output
    private readonly float[] _decodeFloatBuf = new float[CHUNK_FRAMES]; // short→float for ring

    // Cached Expert transform for spatial positioning (Worker side only)
    private PostureHandler remotePosture;

    // Sequence numbers — drive Opus FEC/PLC gap detection on the receive path.
    private ushort _sendSeq        = 0;
    private ushort _expectedSeq    = 0;
    private bool   _seqInitialized = false;

    // Pre-allocate 30 min of recording capacity to prevent mid-session GC pauses.
    private const int RECORDING_CAPACITY = SAMPLE_RATE * 60 * 30;
    private List<float>  localSamples  = new List<float>(RECORDING_CAPACITY);
    private List<float>  remoteSamples = new List<float>(RECORDING_CAPACITY);
    private readonly object remoteSamplesLock = new object();
    private string _wavTimestamp;

    public string LocalWavPath => string.IsNullOrEmpty(saveDir) ? null
        : Path.Combine(saveDir, $"voice_local_{_wavTimestamp}.wav");

    public float CurrentRecordingSeconds => localSamples.Count / (float)SAMPLE_RATE;

    public void Initialize(bool expert, string saveDirectory, string preferredMicDevice = null)
    {
        isExpert         = expert;
        saveDir          = saveDirectory;
        _preferredDevice = preferredMicDevice;
        _wavTimestamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // Opus encoder: SILK wideband, VBR, inband FEC enabled.
        // UseInbandFEC embeds a low-bitrate copy of frame N-1 inside frame N so
        // the receiver can recover a single lost packet without retransmission.
#pragma warning disable CS0618
        _opusEncoder = new OpusEncoder(SAMPLE_RATE, 1, OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CS0618
        _opusEncoder.Bitrate           = OPUS_BITRATE;
        _opusEncoder.UseVBR            = true;
        _opusEncoder.UseInbandFEC      = true;
        _opusEncoder.PacketLossPercent = OPUS_LOSS_PERCENT;

#pragma warning disable CS0618
        _opusDecoder = new OpusDecoder(SAMPLE_RATE, 1);
#pragma warning restore CS0618

        StartMic();
        BuildAudioSource();
        StartCoroutine(MicCaptureLoop());

        Debug.Log($"[VoiceCommunicator] Ready (Opus/UDP) — isExpert={expert}  dir={saveDir}");
    }

    /// <summary>
    /// Wire up the UDP transport. Must be called after Initialize().
    /// OnAudioReceived runs on the UDP receiver background thread —
    /// all shared state access inside OnAudioBytesReceived is lock-protected.
    /// </summary>
    public void SetTransport(UdpAudioTransport transport)
    {
        audioTransport = transport;
        transport.OnAudioReceived = OnAudioBytesReceived;
    }

    private void StartMic()
    {
        try
        {
            bool preferredAvailable = !string.IsNullOrEmpty(_preferredDevice)
                && Array.Exists(Microphone.devices, d => d == _preferredDevice);
            micDevice = preferredAvailable ? _preferredDevice
                : Microphone.devices.Length > 0 ? Microphone.devices[0] : "";
            micClip   = Microphone.Start(micDevice, true, 10, SAMPLE_RATE);
            float t = 0f;
            while (Microphone.GetPosition(micDevice) <= 0 && t < 1f) t += 0.01f;
            lastMicSample = 0;
            Debug.Log($"[VoiceCommunicator] Microphone: {(string.IsNullOrEmpty(micDevice) ? "default" : micDevice)}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoiceCommunicator] Microphone start failed: {ex.Message}");
        }
    }

    private void BuildAudioSource()
    {
        try
        {
            var go       = new GameObject("RemoteVoiceSource");
            remoteSource = go.AddComponent<AudioSource>();

            // Expert hears Worker as flat 2D audio.
            // Worker starts at spatialBlend=0f (2D, always audible) until UpdateSpatialPosition
            // locates the Expert's PostureHandler and positions the source at their head.
            // Starting at 1f with the source at world origin (0,0,0) caused the OVR HRTF
            // to render it at feet level — heavily attenuated and nearly inaudible on Quest.
            remoteSource.spatialBlend = 0f;   // overridden to 1f in UpdateSpatialPosition
            remoteSource.rolloffMode  = AudioRolloffMode.Linear;
            remoteSource.minDistance  = 0.3f;
            remoteSource.maxDistance  = 8f;
            remoteSource.volume       = 1f;
            remoteSource.loop         = true;
            remoteSource.dopplerLevel = 0f;

            var clip = AudioClip.Create("RemoteVoice", SAMPLE_RATE * 5, 1, SAMPLE_RATE,
                                        true, OnPCMRead);
            remoteSource.clip = clip;
            remoteSource.Play();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoiceCommunicator] AudioSource build failed: {ex.Message}");
        }
    }

    // Called on Unity's audio thread — must be lock-protected.
    private void OnPCMRead(float[] data)
    {
        try
        {
            lock (ringLock)
            {
                int buffered = (writePos - readPos + ring.Length) % ring.Length;

                if (!_playbackStarted)
                {
                    if (buffered < TARGET_BUFFER) { Array.Clear(data, 0, data.Length); return; }
                    _playbackStarted = true;
                }

                // Hard snap only as last resort.
                if (buffered > MAX_BUFFER)
                    readPos = (writePos - TARGET_BUFFER + ring.Length) % ring.Length;

                for (int i = 0; i < data.Length; i++)
                {
                    if (writePos == readPos) { for (; i < data.Length; i++) data[i] = 0f; break; }
                    data[i] = ring[readPos];
                    readPos  = (readPos + 1) % ring.Length;
                }

                // Gradual clock-drift correction: skip 1 sample per callback when
                // buffered is above target — imperceptible micro-pitch vs an audible hard snap.
                buffered = (writePos - readPos + ring.Length) % ring.Length;
                if (buffered > TARGET_BUFFER && writePos != readPos)
                    readPos = (readPos + 1) % ring.Length;
            }
        }
        catch { Array.Clear(data, 0, data.Length); }
    }

    private void Update()
    {
        if (!isExpert && remoteSource != null)
            UpdateSpatialPosition();
    }

    private void UpdateSpatialPosition()
    {
        if (remotePosture == null)
        {
            foreach (var ph in FindObjectsByType<PostureHandler>(FindObjectsSortMode.None))
            {
                if (!ph.photonView.IsMine) { remotePosture = ph; break; }
            }
        }
        if (remotePosture != null)
        {
            remoteSource.transform.position = remotePosture.transform.position;
            if (remoteSource.spatialBlend < 1f)
            {
                remoteSource.spatialBlend = 1f;
                Debug.Log("[VoiceCommunicator] Spatial audio activated — Expert PostureHandler found.");
            }
        }
    }

    private IEnumerator MicCaptureLoop()
    {
        var wait = new WaitForSeconds(CHUNK_FRAMES / (float)SAMPLE_RATE);
        while (true)
        {
            yield return wait;
            CaptureMicAndSend();
        }
    }

    private void CaptureMicAndSend()
    {
        if (micClip == null) return;
        try
        {
            int micPos    = Microphone.GetPosition(micDevice);
            int available = (micPos - lastMicSample + micClip.samples) % micClip.samples;

            int sent = 0;
            while (available >= CHUNK_FRAMES && sent < 4)
            {
                micClip.GetData(micChunk, lastMicSample);
                lastMicSample = (lastMicSample + CHUNK_FRAMES) % micClip.samples;
                available    -= CHUNK_FRAMES;

                // float[] → short[] for Opus encoder
                for (int i = 0; i < CHUNK_FRAMES; i++)
                    _pcmShortBuf[i] = (short)Mathf.Clamp(
                        Mathf.RoundToInt(micChunk[i] * 32767f), short.MinValue, short.MaxValue);

#pragma warning disable CS0618
                int encodedLen = _opusEncoder.Encode(
                    _pcmShortBuf, 0, CHUNK_FRAMES, _encodeBuf, 0, _encodeBuf.Length);
#pragma warning restore CS0618

                // Packet layout: [seq:2B LE][opus payload]
                byte[] payload = new byte[2 + encodedLen];
                payload[0] = (byte)(_sendSeq & 0xFF);
                payload[1] = (byte)((_sendSeq >> 8) & 0xFF);
                Buffer.BlockCopy(_encodeBuf, 0, payload, 2, encodedLen);
                _sendSeq++;

                audioTransport?.Send(payload);
                localSamples.AddRange(micChunk); // record raw PCM for WAV
                sent++;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoiceCommunicator] Capture/encode error: {ex.Message}");
        }
    }

    /// <summary>
    /// Called on the UdpAudioTransport receiver background thread.
    /// Packet layout: [seq:2B LE][Opus payload].
    ///
    /// Gap = 0  → normal Opus decode.
    /// Gap = 1  → FEC: the current packet's inband FEC data reconstructs the
    ///            lost frame; then the current packet is decoded normally.
    ///            Recovery is transparent — no audible artifact.
    /// Gap > 1  → (gap-1) PLC frames generated by the Opus decoder from prior
    ///            context, plus 1 FEC recovery for the frame immediately before
    ///            the current packet, then normal decode.
    /// Gap ≥ 20 → assumed sequence discontinuity (sender restart); decoder
    ///            state is reset and the stream resyncs to the current packet.
    /// </summary>
    private void OnAudioBytesReceived(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 3) return;
        try
        {
            ushort seq         = (ushort)(bytes[0] | (bytes[1] << 8));
            int    opusDataLen = bytes.Length - 2;

            if (!_seqInitialized) { _expectedSeq = seq; _seqInitialized = true; }

            int gap = (seq - _expectedSeq + 65536) % 65536;

            if (gap >= 20)
            {
                // Sequence discontinuity — sender likely restarted.
                // Reset decoder state to avoid corrupted audio from stale context.
#pragma warning disable CS0618
                _opusDecoder    = new OpusDecoder(SAMPLE_RATE, 1);
#pragma warning restore CS0618
                _playbackStarted = false;
                _seqInitialized  = false;
                _expectedSeq     = seq;
                gap              = 0;
                Debug.Log($"[VoiceCommunicator] Sequence reset — resyncing to seq {seq}");
            }

            if (gap > 0)
            {
                // PLC for all frames except the one immediately before current (covered by FEC)
#pragma warning disable CS0618
                for (int p = 0; p < gap - 1; p++)
                {
                    _opusDecoder.Decode(null, 0, 0, _decodeBuf, 0, CHUNK_FRAMES, false);
                    ShortToFloat(_decodeBuf, _decodeFloatBuf);
                    lock (ringLock)          WriteChunkToRing(_decodeFloatBuf);
                    lock (remoteSamplesLock) remoteSamples.AddRange(_decodeFloatBuf);
                }

                // FEC: use current packet to recover the frame immediately before it.
                // Opus embeds a compact copy of frame N-1 inside frame N when FEC is enabled.
                _opusDecoder.Decode(bytes, 2, opusDataLen, _decodeBuf, 0, CHUNK_FRAMES, true);
                ShortToFloat(_decodeBuf, _decodeFloatBuf);
                lock (ringLock)          WriteChunkToRing(_decodeFloatBuf);
                lock (remoteSamplesLock) remoteSamples.AddRange(_decodeFloatBuf);
#pragma warning restore CS0618

                if (gap > 1)
                    Debug.LogWarning($"[VoiceCommunicator] {gap} packet(s) lost at seq {_expectedSeq}: {gap - 1} PLC + 1 FEC");
            }

            // Normal decode of current packet
#pragma warning disable CS0618
            _opusDecoder.Decode(bytes, 2, opusDataLen, _decodeBuf, 0, CHUNK_FRAMES, false);
#pragma warning restore CS0618
            ShortToFloat(_decodeBuf, _decodeFloatBuf);
            lock (ringLock)          WriteChunkToRing(_decodeFloatBuf);
            lock (remoteSamplesLock) remoteSamples.AddRange(_decodeFloatBuf);

            _expectedSeq = (ushort)(seq + 1);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoiceCommunicator] Receive/decode error: {ex.Message}");
        }
    }

    private static void ShortToFloat(short[] src, float[] dst)
    {
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] / 32767f;
    }

    // Must be called under ringLock.
    private void WriteChunkToRing(float[] chunk)
    {
        foreach (float s in chunk)
        {
            ring[writePos] = s;
            writePos       = (writePos + 1) % ring.Length;
            if (writePos == readPos) readPos = (readPos + 1) % ring.Length;
        }
    }

    public void SaveRecordings()
    {
        if (string.IsNullOrEmpty(saveDir)) return;
        try { Directory.CreateDirectory(saveDir); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoiceCommunicator] Could not create save dir: {ex.Message}");
            return;
        }
        WriteWav(localSamples, Path.Combine(saveDir, $"voice_local_{_wavTimestamp}.wav"));
        List<float> remoteSnapshot;
        lock (remoteSamplesLock) remoteSnapshot = new List<float>(remoteSamples);
        WriteWav(remoteSnapshot, Path.Combine(saveDir, $"voice_remote_{_wavTimestamp}.wav"));
    }

    private static void WriteWav(List<float> samples, string path)
    {
        if (samples == null || samples.Count == 0)
        {
            Debug.LogWarning($"[VoiceCommunicator] No audio to save: {path}");
            return;
        }
        try
        {
            int count    = samples.Count;
            int byteData = count * 2;
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + byteData);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(SAMPLE_RATE);
            bw.Write(SAMPLE_RATE * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(byteData);
            foreach (float s in samples)
                bw.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
            Debug.Log($"[VoiceCommunicator] Saved: {path}  ({count / SAMPLE_RATE:F1}s)");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VoiceCommunicator] WAV write failed ({path}): {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        try { if (micClip != null) Microphone.End(micDevice); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceCommunicator] Mic end error: {ex.Message}"); }
        try { SaveRecordings(); }
        catch (Exception ex) { Debug.LogWarning($"[VoiceCommunicator] Auto-save error: {ex.Message}"); }
    }
}
