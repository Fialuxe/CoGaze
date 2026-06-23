using System.Collections;
using UnityEngine;
using Unity.WebRTC;

// Persistent host for WebRTC.Update() event loop.
// Created once with DontDestroyOnLoad — survives scene reloads and individual
// WebRtcVideoSession lifecycles so the pump never becomes orphaned.
public class WebRtcPumpHost : MonoBehaviour
{
    private void Start() => StartCoroutine(WebRTC.Update());
}
