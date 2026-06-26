#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections;
using UnityEngine;

/// <summary>
/// Shared Meta Touch controller haptic helper.
///
/// Fires a vibration pulse on one or more controllers and schedules it to stop
/// after <c>duration</c> seconds, using the calling MonoBehaviour as the coroutine
/// host. Consolidates the StopVibration coroutine boilerplate that was previously
/// duplicated across WorkerStartupPanel, SetupCoordinator, WorkerHUD2 and
/// QuestionnairePokeInput.
///
/// Android-only: OVRInput is part of the Oculus integration and is not referenced
/// on the Expert (Standalone) build, matching the existing per-call #if guards.
/// </summary>
public static class OvrHaptics
{
    /// <summary>
    /// Pulse <paramref name="controllers"/> then auto-stop after <paramref name="duration"/>
    /// seconds. Argument order matches OVRInput.SetControllerVibration(frequency, amplitude,
    /// controller). The coroutine runs on <paramref name="host"/>, so it shares the host's
    /// lifetime (identical to the inline StartCoroutine(StopVibration(...)) it replaces).
    /// </summary>
    public static void Pulse(MonoBehaviour host, float frequency, float amplitude, float duration,
                             params OVRInput.Controller[] controllers)
    {
        if (host == null || controllers == null || controllers.Length == 0) return;
        foreach (var c in controllers)
            OVRInput.SetControllerVibration(frequency, amplitude, c);
        host.StartCoroutine(StopAfter(duration, controllers));
    }

    private static IEnumerator StopAfter(float delay, OVRInput.Controller[] controllers)
    {
        yield return new WaitForSeconds(delay);
        foreach (var c in controllers)
            OVRInput.SetControllerVibration(0f, 0f, c);
    }
}
#endif
