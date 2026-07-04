using System;
using UnityEngine;
using Photon.Voice;
using Photon.Voice.Unity;
using POpusCodec.Enums;

// Configures the Worker role's rig on the local player GameObject; called once by SceneBootstrapper2.
internal static class WorkerRigBuilder
{
    internal struct Result
    {
        internal WorkerVideoStream VideoStream;
        internal VoiceRecorder     VoiceRecorder;
        internal SetupCoordinator  SetupCoordinator;
    }

    internal static Result Build(
        GameObject             playerObj,
        ExperimentManager2     expMgr,
        string                 micDevice,
        string                 participantId,
        int                    participantOrderIndex,
        int                    requiredTaskQRCount,
        Action<byte, string[]> raiseSignal)
    {
        // Disable default camera; ensure AudioListener remains in scene
        var cam = Camera.main;
        if (cam != null && cam.GetComponentInParent<OVRCameraRig>() == null)
        {
            // Check if any AudioListener exists OUTSIDE the camera hierarchy.
            // If all listeners are on the camera (or its children), create a standalone one
            // before disabling the camera — otherwise audio goes silent.
            bool hasExternalAL = false;
            foreach (var al in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (!al.transform.IsChildOf(cam.transform))
                { hasExternalAL = true; break; }
            }
            if (!hasExternalAL)
                new GameObject("WorkerAudioListener").AddComponent<AudioListener>();
            cam.gameObject.SetActive(false);
        }

        // PostureHandler + MetaXRPostureInput
        var postureInput   = playerObj.AddComponent<MetaXRPostureInput>();
        var postureHandler = playerObj.GetComponent<PostureHandler>();
        if (postureHandler != null) postureHandler.Initialize(postureInput);

        // GazeVisualizer — renders the remote Expert's shared gaze on the Worker
        new GameObject("LocalGazeVisualizer").AddComponent<GazeVisualizer>().Initialize();
        FileLogger.Log("Setup", "[SceneBootstrapper2] Worker GazeVisualizer spawned.");

        // Hide own avatar from self
        foreach (var r in playerObj.GetComponentsInChildren<MeshRenderer>(true))
            r.enabled = false;

        // ExperimentManager2
        expMgr.Initialize(isExpert: false);

        // WorkerHUD2
        var hud = playerObj.AddComponent<WorkerHUD2>();
        hud.Initialize(expMgr);

        // Wire the HUD's (previously caller-less) calibration + identification hooks.
        //  - ConnectMeshHandler: dual-QR calib staging UI + confirm haptic/flash.
        //  - ConnectIdentificationTask: shows the QR identification instruction and proximity/confirm
        //    feedback — without this the subject's only core action was never reflected on the HUD.
        var meshHandler = UnityEngine.Object.FindAnyObjectByType<MeshHandler>();
        if (meshHandler != null) hud.ConnectMeshHandler(meshHandler);
        else Debug.LogWarning("[SceneBootstrapper2] MeshHandler not found — Worker calibration HUD feedback disabled.");

        var idTask = UnityEngine.Object.FindAnyObjectByType<IdentificationTask>();
        if (idTask != null) hud.ConnectIdentificationTask(idTask);
        else Debug.LogWarning("[SceneBootstrapper2] IdentificationTask not found — Worker identification instruction disabled.");

        // SetupCoordinator — drives setup progress UI and tracks calib + task QR conditions
        var setupCoord = playerObj.AddComponent<SetupCoordinator>();
        setupCoord.Initialize(isWorker: true, expMgr, requiredTaskQRCount);

        // TutorialGuide — self-guided paged tutorial (A-button pages + grip practice) shown during
        // the Tutorial state; replaces the operator's verbal briefing and notifies the Expert on
        // completion.
        playerObj.AddComponent<TutorialGuide>().Initialize(expMgr);

        // ExpertAvatarHider — hides the Expert's (fixed-pose) avatar while the Assembly task runs.
        playerObj.AddComponent<ExpertAvatarHider>().Initialize(expMgr);

        // Photon Voice 2 — Recorder must be on the prefab; we configure it here
        var recorder = playerObj.GetComponentInChildren<Recorder>();
        if (recorder != null && !string.IsNullOrEmpty(micDevice))
            recorder.MicrophoneDevice = new DeviceInfo(micDevice);
        else if (recorder == null)
            Debug.LogWarning("[SceneBootstrapper2] Recorder not found on LocalWorker prefab.");

        if (recorder != null)
        {
            recorder.MicrophoneType = Recorder.MicType.Photon;
            // AGC off so VAD threshold is predictable (native AGC raises gain during silence → false triggers)
            recorder.SetAndroidNativeMicrophoneSettings(aec: true, agc: false, ns: true);
            var dsp = recorder.gameObject.GetComponent<WebRtcAudioDsp>()
                      ?? recorder.gameObject.AddComponent<WebRtcAudioDsp>();
            dsp.AEC = false; dsp.NoiseSuppression = false; dsp.AGC = false;
            recorder.SamplingRate            = SamplingRate.Sampling16000;
            recorder.FrameDuration           = OpusCodec.FrameDuration.Frame20ms;
            recorder.Bitrate                 = 24000;
            recorder.VoiceDetection          = true;
            recorder.VoiceDetectionThreshold = 0.015f;
            recorder.VoiceDetectionDelayMs   = 500;
        }

        // VoiceRecorder — WAV recording independent of PV2
        string logDir = System.IO.Path.Combine(Application.persistentDataPath, "logs", participantId);
        var voiceRecorder = playerObj.AddComponent<VoiceRecorder>();
        voiceRecorder.Initialize(false, logDir, micDevice);

        // WorkerVideoStream — WebRTC signaling wired via raiseSignal delegate
        var videoStream = playerObj.AddComponent<WorkerVideoStream>();
        videoStream.Initialize(expMgr);

        var s = videoStream.Session;
        s.OnSendOffer  += sdp => raiseSignal(WebRtcVideoSession.EVT_OFFER,  new[] { sdp });
        s.OnSendAnswer += sdp => raiseSignal(WebRtcVideoSession.EVT_ANSWER, new[] { sdp });
        s.OnSendIce    += (c, mid, idx) => raiseSignal(WebRtcVideoSession.EVT_ICE, new[] { c, mid, idx.ToString() });

        // QuestionnaireManager — set participant identity so JSON filenames are correct
        var qm = UnityEngine.Object.FindAnyObjectByType<QuestionnaireManager>();
        if (qm != null)
        {
            qm.participantId     = participantId;
            qm.participantNumber = participantOrderIndex;
            FileLogger.Log("Setup", $"[SceneBootstrapper2] QuestionnaireManager participant set: id={participantId} num={participantOrderIndex}");
        }
        else
        {
            Debug.LogWarning("[SceneBootstrapper2] QuestionnaireManager not found in scene — questionnaire data will use default participant identity.");
        }

        // WorkerTrackingSync — publishes head/controller pose to Photon custom player properties
        playerObj.AddComponent<WorkerTrackingSync>();
        FileLogger.Log("Setup", "[SceneBootstrapper2] WorkerTrackingSync added.");

        return new Result
        {
            VideoStream      = videoStream,
            VoiceRecorder    = voiceRecorder,
            SetupCoordinator = setupCoord,
        };
    }
}
