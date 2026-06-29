using System.Collections.Generic;
using UnityEngine;

// Captures incoming PCM via OnAudioFilterRead into a shared _buffer for WAV recording; audio passes through unchanged.
[DisallowMultipleComponent]
public class RemoteAudioCapture : MonoBehaviour
{
    private List<float> _buffer;
    private object      _bufferLock;

    public int SampleRate { get; private set; }

    public void Initialize(List<float> sharedBuffer, object sharedLock)
    {
        _buffer     = sharedBuffer;
        _bufferLock = sharedLock;
        // OnAudioFilterRead always runs at the output/DSP rate (see Photon SpeakerAudioFilterRead).
        SampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (_buffer == null || _bufferLock == null) return;
        lock (_bufferLock)
        {
            if (channels == 1)
            {
                _buffer.AddRange(data);
            }
            else
            {
                for (int i = 0; i < data.Length; i += channels)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++) sum += data[i + c];
                    _buffer.Add(sum / channels);
                }
            }
        }
        // data is passed through unmodified — audio continues to play normally
    }
}
