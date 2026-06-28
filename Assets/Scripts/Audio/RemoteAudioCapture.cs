using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attached to the Speaker's AudioSource GameObject by VoiceRecorder.AttachRemoteCapture().
/// Captures incoming PCM on the audio thread via OnAudioFilterRead and appends it to the
/// shared remoteSamples buffer for WAV recording.  Audio passes through unchanged.
/// </summary>
[DisallowMultipleComponent]
public class RemoteAudioCapture : MonoBehaviour
{
    private List<float> buffer;
    private object      bufferLock;

    /// <summary>
    /// Rate (Hz) at which OnAudioFilterRead delivers samples — i.e. the DSP/output rate, NOT 16kHz.
    /// Captured on the main thread in Initialize so VoiceRecorder can write a correct WAV header for
    /// the remote stream. Samples are mono-downmixed below, so the remote WAV is 1 channel.
    /// </summary>
    public int SampleRate { get; private set; }

    public void Initialize(List<float> sharedBuffer, object sharedLock)
    {
        buffer     = sharedBuffer;
        bufferLock = sharedLock;
        // OnAudioFilterRead always runs at the output/DSP rate (see Photon SpeakerAudioFilterRead).
        SampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (buffer == null || bufferLock == null) return;
        lock (bufferLock)
        {
            if (channels == 1)
            {
                buffer.AddRange(data);
            }
            else
            {
                for (int i = 0; i < data.Length; i += channels)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++) sum += data[i + c];
                    buffer.Add(sum / channels);
                }
            }
        }
        // data is passed through unmodified — audio continues to play normally
    }
}
