#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using Photon.Voice;

// Maps a Unity Microphone device name (what StartupUI lists and StartupConfig stores) to the
// native Windows device that Photon's MicType.Photon capture (WindowsAudioInPusher) actually
// opens. The two APIs use different ID spaces: Unity identifies mics by name string, native
// AudioIn.dll by enumeration index. A Recorder.MicrophoneDevice built with the string
// DeviceInfo constructor carries IDInt=0, so the pusher opens native device index 0 regardless
// of the operator's selection (on a Quest Link PC that is typically the silent Oculus Virtual
// Audio Device). Resolving through Photon's own AudioInEnumerator guarantees the index we set
// is in the same ID space the pusher consumes.
public static class PhotonMicDeviceResolver
{
    /// <summary>
    /// Resolves a Unity mic name to a native Photon DeviceInfo (int-ID). Returns false with a
    /// human-readable reason in <paramref name="detail"/> when no confident match exists;
    /// callers should then fall back to DeviceInfo.Default (native default capture device).
    /// </summary>
    public static bool TryResolve(string unityMicName, out DeviceInfo nativeDevice, out string detail)
    {
        nativeDevice = DeviceInfo.Default;

        if (string.IsNullOrEmpty(unityMicName))
        {
            detail = "no device name given";
            return false;
        }

        List<DeviceInfo> devices;
        try
        {
            using (var enumerator = new Photon.Voice.Windows.AudioInEnumerator(new Photon.Voice.Unity.Logger()))
            {
                if (enumerator.Error != null)
                {
                    detail = "native enumeration failed: " + enumerator.Error;
                    return false;
                }
                devices = new List<DeviceInfo>(enumerator);
            }
        }
        catch (Exception e)
        {
            detail = "native enumeration failed: " + e.Message;
            return false;
        }

        if (devices.Count == 0)
        {
            detail = "native enumeration returned no capture devices";
            return false;
        }

        // Exact name match first.
        foreach (var d in devices)
        {
            if (string.Equals(d.Name, unityMicName, StringComparison.Ordinal))
            {
                nativeDevice = d;
                detail = "exact name match";
                return true;
            }
        }

        // Unity truncates long device names, so accept a prefix relation in either direction —
        // but only when it is unambiguous (a truncated name can collide across similar devices).
        DeviceInfo prefixMatch = default;
        int prefixMatches = 0;
        foreach (var d in devices)
        {
            if (!string.IsNullOrEmpty(d.Name)
                && (d.Name.StartsWith(unityMicName, StringComparison.Ordinal)
                    || unityMicName.StartsWith(d.Name, StringComparison.Ordinal)))
            {
                prefixMatch = d;
                prefixMatches++;
            }
        }
        if (prefixMatches == 1)
        {
            nativeDevice = prefixMatch;
            detail = "prefix name match";
            return true;
        }

        detail = prefixMatches > 1
            ? $"ambiguous: {prefixMatches} native devices share the name prefix"
            : $"'{unityMicName}' not found among {devices.Count} native devices: {string.Join(" | ", NamesOf(devices))}";
        return false;
    }

    private static IEnumerable<string> NamesOf(List<DeviceInfo> devices)
    {
        foreach (var d in devices) yield return d.ToString();
    }
}
#endif
