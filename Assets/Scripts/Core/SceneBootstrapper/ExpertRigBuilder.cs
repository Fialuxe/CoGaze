using System;
using UnityEngine;
using Photon.Pun;
using Photon.Voice;
using Photon.Voice.Unity;
using POpusCodec.Enums;
using Hashtable = ExitGames.Client.Photon.Hashtable;

// Configures Expert rig (input, audio, video) on the local player GameObject; called once by SceneBootstrapper2.SetupExpert.
internal static class ExpertRigBuilder
{
    internal struct Result
    {
        internal ExpertVideoDisplay VideoDisplay;
        internal VoiceRecorder      VoiceRecorder;
    }

    internal static Result Build(
        GameObject             playerObj,
        ExperimentManager2     expMgr,
        string                 micDevice,
        string                 participantId,
        int                    participantOrderIndex,
        int                    requiredTaskQRCount,
        Action<byte, string[]> raiseSignal,
        bool                   isOfflineMode)
    {
        // Remove OVRCameraRig — Expert is PC only
        var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
        if (rig != null) { rig.gameObject.SetActive(false); UnityEngine.Object.Destroy(rig.gameObject); }

        if (UnityEngine.Object.FindAnyObjectByType<AudioListener>() == null)
            new GameObject("AudioListener").AddComponent<AudioListener>();

        var connHandler = playerObj.GetComponent<ConnectionHandler>();
        if (connHandler == null) connHandler = playerObj.AddComponent<ConnectionHandler>();

        // GazeHandler + OscGazeInput
        var gazeInput   = playerObj.AddComponent<OscGazeInput>();
        var gazeHandler = playerObj.GetComponent<GazeHandler>();
        if (gazeHandler != null)
        {
            gazeHandler.Initialize(gazeInput);
            expMgr.SetGazeHandler(gazeHandler);  // condition switches will update gaze mode

            // Ensure GazeHandler is observed by Photon so gaze data is synced to the Worker.
            // The prefab inspector setting may be missing; add defensively at runtime.
            var pv = playerObj.GetComponent<PhotonView>();
            if (pv != null && !pv.ObservedComponents.Contains(gazeHandler))
            {
                pv.ObservedComponents.Add(gazeHandler);
                Debug.Log("[ExpertRigBuilder] GazeHandler added to PhotonView.ObservedComponents.");
            }
        }
        else
        {
            Debug.LogError("[ExpertRigBuilder] GazeHandler not found on Expert prefab — gaze visualization will not work on Worker.");
        }

        // ExperimentManager2
        expMgr.Initialize(isExpert: true);

        // Re-init the gaze-mode key lock from the CURRENT experiment state immediately. The 1/2/3
        // keys must stay locked for the whole run; a fresh ConnectionHandler defaults to unlocked and
        // only updates on the next OnStateChanged. On a mid-run reconnect that event may not fire for
        // a long time, leaving the operator able to silently overwrite the next condition's gaze
        // format. ExperimentManager2 survives the reconnect, so its CurrentState is authoritative.
        connHandler.LockGazeModeKeys =
            expMgr.CurrentState != ExperimentState.Idle && expMgr.CurrentState != ExperimentState.Setup;

        // WebcamCalibrationUI — Unity-driven 16-point calibration overlay (Expert PC only)
        var calibUiGo = new UnityEngine.GameObject("WebcamCalibrationUI");
        calibUiGo.transform.SetParent(playerObj.transform, false);
        var calibUi = calibUiGo.AddComponent<WebcamCalibrationUI>();
        expMgr.SetWebcamCalibUI(calibUi);

        // ExpertUI2
        var ui = playerObj.AddComponent<ExpertUI2>();
        ui.Initialize(expMgr);

        // SetupCoordinator — shows Worker status panel and approve button during Setup state
        var setupCoord = playerObj.AddComponent<SetupCoordinator>();
        setupCoord.Initialize(isWorker: false, expMgr, requiredTaskQRCount);

        // Photon Voice 2 — Recorder must be on the prefab; configure it here
        var recorder = playerObj.GetComponentInChildren<Recorder>();
        if (recorder == null)
            Debug.LogWarning("[SceneBootstrapper2] Recorder not found on RemoteExpert prefab.");

        if (recorder != null)
        {
            // Native (WASAPI) capture, NOT Unity Microphone. VoiceRecorder opens the same device via
            // Unity's Microphone API, and Unity mic capture is a per-device singleton: whichever side
            // calls Microphone.Start last steals the device and freezes the other side's AudioClip.
            // PV2's MicWrapper then keeps re-reading its frozen 1-second clip against the still-
            // advancing device position — the Worker hears the last captured second repeated forever
            // at irregular intervals. Photon native capture bypasses the Unity singleton entirely
            // (same fix the Worker already uses for the Android mic-contention issue).
            recorder.MicrophoneType = Recorder.MicType.Photon;

            // The native pusher only reads DeviceInfo.IDInt; a string-constructed DeviceInfo (Unity
            // name) carries IDInt=0 and silently opens native device index 0 — on a Quest Link PC
            // that is the mute Oculus Virtual Audio Device, so the Worker hears nothing. Resolve the
            // operator's Unity-name selection to a native enumeration ID; if that fails, fall back
            // to the native default capture device (Windows' own default mic) rather than index 0.
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!string.IsNullOrEmpty(micDevice))
            {
                if (PhotonMicDeviceResolver.TryResolve(micDevice, out var nativeMic, out string micDetail))
                {
                    recorder.MicrophoneDevice = nativeMic;
                    Debug.Log($"[ExpertRigBuilder] Photon native mic {nativeMic} resolved from '{micDevice}' ({micDetail}).");
                }
                else
                {
                    recorder.MicrophoneDevice = DeviceInfo.Default;
                    Debug.LogError($"[ExpertRigBuilder] Could not map mic '{micDevice}' to a native device ({micDetail}). " +
                                   "Falling back to the Windows default capture device — verify the Worker hears the Expert.");
                }
            }
            else
            {
                recorder.MicrophoneDevice = DeviceInfo.Default;
            }
#else
            if (!string.IsNullOrEmpty(micDevice))
                recorder.MicrophoneDevice = new DeviceInfo(micDevice);
#endif

            // On failure the Recorder would otherwise silently fall back to Unity Microphone and
            // fight VoiceRecorder for the device (the freeze-loop bug all over again). Fail loud:
            // no audio + an error log is diagnosable, a re-frozen loop mid-experiment is not.
            recorder.UseMicrophoneTypeFallback = false;

            var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                      ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
            dsp.AEC = false; dsp.NoiseSuppression = true; dsp.AGC = true;
            dsp.AgcCompressionGain = 18; dsp.AgcTargetLevel = 3;
            // WindowsAudioInPusher captures at a fixed 16 kHz (Voice Capture DSP hardcode); the
            // encoder rate must match or PV2 inserts a FramerResampler upsampling 16k→48k for
            // nothing. The old 48000 comment ("PC mic does not support 16000") only applied to the
            // Unity Microphone path, which is no longer used here.
            recorder.SamplingRate  = SamplingRate.Sampling16000;
            recorder.FrameDuration = OpusCodec.FrameDuration.Frame20ms;
            recorder.Bitrate       = 24000;
        }

        // VoiceRecorder — WAV recording
        string logDir = System.IO.Path.Combine(Application.persistentDataPath, "logs", participantId);
        var voiceRecorder = playerObj.AddComponent<VoiceRecorder>();
        voiceRecorder.Initialize(true, logDir, micDevice);

        // GazeVisualizer (self-view)
        new GameObject("LocalGazeVisualizer").AddComponent<GazeVisualizer>().Initialize();

        if (!isOfflineMode)
        {
            PhotonNetwork.CurrentRoom?.SetCustomProperties(new Hashtable
            {
                { "participantId", participantId }
            });
        }

        // ExpertVideoDisplay — WebRTC answerer; signaling wired via raiseSignal delegate
        var videoDisplay = playerObj.AddComponent<ExpertVideoDisplay>();
        videoDisplay.Initialize(expMgr);

        var s = videoDisplay.Session;
        s.OnSendOffer  += sdp => raiseSignal(WebRtcVideoSession.EVT_OFFER,  new[] { sdp });
        s.OnSendAnswer += sdp => raiseSignal(WebRtcVideoSession.EVT_ANSWER, new[] { sdp });
        s.OnSendIce    += (c, mid, idx) => raiseSignal(WebRtcVideoSession.EVT_ICE, new[] { c, mid, idx.ToString() });

        // Signal Worker that signaling is ready — Worker waits for this before calling TriggerOffer()
        if (!isOfflineMode)
        {
            Debug.Log("[SceneBootstrapper2] Setting expertReady=true — Worker can now send WebRTC offer.");
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { ["expertReady"] = true });
            // PublishExpertSetupReadyLoop is a coroutine — started by the shell (SceneBootstrapper2) after this method returns
        }

        // ExperimentLogger — writes trials.csv / frames.csv / replay JSON
        var logger = playerObj.AddComponent<ExperimentLogger>();
        logger.Initialize(expMgr, participantOrderIndex, logDir);

        return new Result { VideoDisplay = videoDisplay, VoiceRecorder = voiceRecorder };
    }
}
