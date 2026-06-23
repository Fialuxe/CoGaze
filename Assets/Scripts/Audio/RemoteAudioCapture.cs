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

    public void Initialize(List<float> sharedBuffer, object sharedLock)
    {
        buffer     = sharedBuffer;
        bufferLock = sharedLock;
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
