#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections;
using UnityEngine;

// Android-only haptic helper; consolidates StopVibration boilerplate from WorkerStartupPanel, SetupCoordinator, WorkerHUD2, QuestionnairePokeInput.
public static class OvrHaptics
{
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
