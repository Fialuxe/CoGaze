# CoGaze

CoGaze is a networked remote-collaboration experiment system that connects a **Worker** on a Meta Quest 3 with a **Remote Expert** on a PC. The Expert's eye gaze is visualized in the Worker's VR view in real time. The system runs a structured 10-condition experiment (tracking method × gaze visualization mode, plus a no-gaze baseline) and records detailed trial data for analysis.

CoGazeは、Meta Quest 3を装着した**作業者（Worker）**と、PCを使う**遠隔専門家（Expert）**を繋ぐリアルタイム遠隔協調実験システムです。専門家の視線が作業者のVRビュー内にリアルタイムで可視化されます。視線取得方法 × 視線可視化モード（＋視線なしベースライン）からなる10条件実験を実施し、詳細なトライアルデータを記録します。

---

## 📖 System Overview / システム概要

Built on **Unity 6** with **Photon PUN 2** for signaling and role-based state sync, **Photon Voice 2** for real-time voice, and **Unity WebRTC (H.264)** for low-latency video.

> ℹ️ Earlier versions used raw UDP for JPEG video and a Concentus/Opus voice stack. Both were replaced by Photon Voice 2 + Unity WebRTC. The legacy transport classes have been removed.

### Roles / 役割

| Role | Platform | Responsibilities |
|------|----------|-----------------|
| **Worker** | Meta Quest 3 (Android) | Wears HMD, performs identification & assembly tasks, sees Expert's gaze, streams camera video (WebRTC), scans QR for spatial calibration |
| **Expert** | Windows PC | Runs experiment, controls step flow, streams eye gaze from Tobii/webcam via OSC, views Worker video |

Role is auto-detected at runtime:
- If a `RoleBasedBootSystem` component is present, its `SelectedRole` is used (useful for testing the Worker path in the Editor).
- Otherwise: **Android build → Worker**, **Standalone/PC → Expert**.

---

## ⚙️ Prerequisites / 前提条件

### Unity Project
- **Unity 6** (developed on `6000.0.73f1`) with the **Android Build Support** module
- **Photon PUN 2** — configure `AppIdRealtime` in `PhotonServerSettings`
- **Photon Voice 2** — configure `AppIdVoice` in `PhotonServerSettings` (separate App ID from Realtime)
- **Unity WebRTC** (`com.unity.webrtc` 3.0.0) — H.264 hardware video encode/decode
- **Meta XR SDK** — OVRCameraRig, OVRSkeleton, MR Utility Kit (MRUK, for QR tracking)
- **Newtonsoft.Json** — via Unity's `com.unity.nuget.newtonsoft-json` package

### Expert PC
- Windows (x64)
- **Python eye-tracking process** run **separately** — Unity connects to it over OSC (it no longer launches Python itself). Start it before / alongside the experiment.
  - **Python 32-bit** for Tobii infrared, **Python 64-bit** for webcam scripts, as required by your tracker
  - **EyeTrackToOSCData** repository — see [EyeTrackToOSCData by Fialuxe](https://github.com/Fialuxe/EyeTrackToOSCData)
- Headphones (the experiment protocol assumes headphones; Expert-side acoustic echo cancellation is intentionally off)

### Worker (Quest)
- Meta Quest 3 / 3S with developer mode enabled
- Permissions in `Assets/Plugins/Android/AndroidManifest.xml`: `RECORD_AUDIO`, `CAMERA`, `USE_SCENE`, `USE_ANCHOR_API`, `HEADSET_CAMERA` (QR/passthrough camera), `HAND_TRACKING`

---

## 🚀 Setup & Usage / セットアップと使用方法

The production scene is **`ExperimentScene`**, driven by **`SceneBootstrapper2`**.

### 1. Pre-placed scene objects
`SceneBootstrapper2` expects these in the scene (see its class doc):
`[Bootstrapper]` (this script + optional `RoleBasedBootSystem`), `OVRCameraRigSetup`, `[Managers]/ExperimentManager` (→ `ExperimentManager2`), `QRSpatialManager`, `QuestionnaireManager`, `OscSessionManager`, and the `[Tasks]` objects (`IdentificationTask`, `AssemblyTask`).

### 2. Configuration (startup, not Inspector)
Run-time settings live in **`StartupConfig`** (participant id/order, microphone, Python host, offline mode), edited via the on-screen **`StartupUI`** (`ParticipantConfigUI` + `ConnectionConfigUI`) on the **PC at launch**. The config is persisted to disk, so the **Quest boots headless** and reads the pre-written config — there is no Quest-side setup panel.

### 3. Build for Quest (Worker)
1. Switch platform to **Android**, Minimum API Level 29+
2. Verify the permissions above are in `AndroidManifest.xml`
3. Build & deploy to the Quest
4. View on-device logs with `adb logcat -s Unity` (look for `[SceneBootstrapper2]`, `[QRSpatialManager]`, `[OSC]` tags)

### 4. Run on PC (Expert)
1. Set `RoleBasedBootSystem → Selected Role = Expert` (or just run a Standalone/PC build)
2. Start the Python eye-tracking process so OSC is available (Unity waits ~10 s, then proceeds without OSC if it is absent)
3. Press Play / run the executable, confirm the startup config, and begin

### Expert keyboard controls
| Key | Action |
|-----|--------|
| `Enter` | Start / advance after a task or noise step / end a questionnaire |
| `Delete` | Force-skip the current running step |

---

## 🎛 Experiment Design / 実験デザイン

**10 conditions** (`Assets/Scripts/Experiment/ExperimentDesign.cs`):

| Index | Tracking method (`ConditionType`) | Gaze mode |
|-------|-----------------------------------|-----------|
| 0–2 | **IR** (Tobii infrared) | Ray / Circle / Frustum |
| 3–5 | **Webcam** | Ray / Circle / Frustum |
| 6–8 | **WebcamFiltered** | Ray / Circle / Frustum |
| 9 | **NoGaze** (baseline) | None |

Gaze visualization modes: `Ray` / `Circle` / `Frustum` / `None`.

Counterbalancing is **two-level**: the four tracking groups (IR / Webcam / WebcamFiltered / NoGaze) are ordered by a **Williams balanced Latin square** (`participantOrderIndex`, row 0–9), and gaze modes are rotated within each group. `ExperimentDesign.GenerateOrder()` returns the 10 condition indices in presentation order.

### Step types in `StreamingAssets/instructions*.txt`
| Type keyword | Behavior |
|---|---|
| `noise` | White-noise playback, auto-ends after its duration |
| `task` | Identification task (QR scan + grip-to-confirm), countdown timer |
| `assembly` | Assembly task — Expert camera follows Worker; Worker video streamed to Expert |
| `alignment` | Expert teleports to the Worker's position; Enter to advance |
| `questionnaire` | NASA-TLX questionnaire gate |

Step blocks are separated by `===`; use `[remote]` / `[local]` markers within a block to show different text to the Expert and the Worker.

---

## 🌐 Network Architecture / ネットワーク構成

| Channel | Transport | Direction |
|---------|-----------|-----------|
| State / events / RPCs | Photon PUN 2 (cloud) | Both ways |
| Voice | Photon Voice 2 (Opus) | Both ways |
| Video | Unity WebRTC (H.264) | Worker → Expert |
| WebRTC signaling (SDP/ICE) | Photon `RaiseEvent` | Both ways |
| Gaze input (OSC) | UDP `9000` (recv) / `9001` (send) | Python ↔ Expert |

- **WebRTC signaling** is tunneled through Photon `RaiseEvent` codes — `EVT_OFFER=60`, `EVT_ANSWER=61`, `EVT_ICE=62`, `EVT_HANGUP=63` (`WebRtcVideoSession`). The Worker is the offerer; only it re-initiates on connection failure (ICE restart first, then a full re-offer).
- **State sync & gameplay** use Photon RPCs: `RPC_ReceiveMeshTransform`, `RPC_IdentificationDone`, `RPC_QuestionnaireComplete`, `RPC_ReceiveQRMarker` (plus Worker hand-bone broadcasting during Task steps).
- **OSC** (`OscSessionManager`): Unity→Python on `9001` (`/session/start`, `/calibration/start`, `/experiment/trial_start|trial_end`, `/ping`…), Python→Unity on `9000` (`/experiment/ack`, `/calibration/result`, `/face/metrics`, `/pong`). Port `9000` is **shared** with `OscGazeInput` (the `/gaze` stream) — a single UDP socket, set up before `OscSessionManager` binds.

---

## 🎙 Audio Pipeline / 音声パイプライン

Voice uses **Photon Voice 2** (Opus), tuned for low latency:

- **Worker (Quest)**: Opus 16 kHz, 20 ms frames, 24 kbps. Uses the Photon mic type so **hardware AEC/AGC/NS** activate; the software `WebRtcAudioDsp` duplicates are disabled to avoid double-processing ("underwater" timbre / gain pumping).
- **Expert (PC)**: Opus **48 kHz** (PC mics don't support 16 kHz), 20 ms, 24 kbps. Software DSP: **NS + AGC on, AEC off** (headphones assumed).
- **Playback**: `PunVoiceClient.SpeakerPrefab` must be set, and is connected explicitly after the PUN room join to avoid an "AppId" race. Remote audio is found via `PhotonVoiceView.SpeakerInUse` (bounded 30 s search): Expert hears the Worker 2D (`spatialBlend=0`); Worker hears the Expert as 3D HRTF positioned at the Expert's head.
- **WiFi low-latency lock** (`WIFI_MODE_FULL_LOW_LATENCY`) is acquired on Quest to disable AP power-saving (eliminates up to ~500 ms packet batching).
- **WAV recording**: `VoiceRecorder` (+ `RemoteAudioCapture`) saves local and remote voice to 16-bit mono WAV, independent of Photon Voice.

---

## 📊 Data Logging / データログ

All files are written to `{logBaseDirectory}/P{n}/` (default `Application.persistentDataPath/logs/P{n}/`). Logging runs on the **Expert PC** only.

| File | Content |
|------|---------|
| `trials.csv` | One row per trial: trial ID, participant, condition index, gaze mode, tracking method, step type, step index, start/end timestamps (ms), duration |
| `frames.csv` | Per-frame: trial ID, timestamp, elapsed seconds, gaze vector, Worker head pose, Expert head pose |
| `replay_{id}.json` | Per-trial replay: metadata + per-frame gaze, head poses, hand bone data |
| `voice_local_{ts}.wav` | Expert's microphone |
| `voice_remote_{ts}.wav` | Worker's voice as received |

Runtime diagnostic logs (`Debug.Log` + `FileLogger`) are also captured to a timestamped `cogaze_*.log` (`UnityLogCapture`); on Android this uses `Application.persistentDataPath` (the APK path is read-only).

---

## ▶️ Replay / リプレイ

Open the replay scene to review recorded trials. Set the log folder to a `P{n}` directory to load the trial list; an IMGUI panel provides selection and playback. Drivers: `ReplayGazeDriver`, `ReplayHandDriver` (`Assets/Scripts/Replay/`).

---

## 🏗 Architecture / アーキテクチャ

> The `*2` files (`SceneBootstrapper2`, `ExperimentManager2`, `ExpertUI2`, `WorkerHUD2`) are the **current** 10-condition system used by `ExperimentScene`. The non-`2` originals are retained for the legacy `SampleScene` only.

```
Assets/Scripts/
├── Audio/
│   ├── AudioDeviceChecker.cs      # Pre-experiment mic check
│   ├── VoiceRecorder.cs           # WAV recording (local + remote), independent of Photon Voice
│   └── RemoteAudioCapture.cs      # Taps a remote Speaker's AudioSource for WAV capture
├── Core/
│   ├── NetworkManager.cs          # Photon connect + room join/reconnect
│   ├── RoleManager.cs             # Role constants & Photon property helpers
│   ├── MessageBank.cs
│   └── SceneBootstrapper/
│       ├── SceneBootstrapper2.cs  # Entry point: config, role detect, XR, Photon, per-role setup
│       ├── LocalWorkerSetup.cs    # Quest-side init (Photon Voice, WebRTC offerer, WiFi lock)
│       ├── RemoteExpertSetup.cs   # PC-side init (Photon Voice, WebRTC answerer, OSC gaze)
│       ├── RoleBasedBootSystem.cs # Editor role override
│       ├── StartupConfig.cs       # Persisted launch settings
│       ├── StartupUI.cs / ParticipantConfigUI.cs / ConnectionConfigUI.cs  # PC startup panel
│       └── SceneBootstrapper.cs   # Legacy (SampleScene)
├── Experiment/
│   ├── ExperimentManager2.cs      # State machine, Williams counterbalancing, OSC-driven flow
│   ├── ExperimentDesign.cs        # 10 condition definitions + Latin-square tables
│   ├── ExperimentLogger.cs        # trials.csv, frames.csv, replay JSON
│   ├── ExpertUI2.cs               # Screen-space overlay (Expert)
│   ├── WorkerHUD2.cs              # World-space HUD (Worker)
│   ├── WorkerHandBroadcaster.cs   # Hand bone data over Photon (Quest, during Task)
│   └── ExperimentManager.cs / ExpertUI.cs / WorkerHUD.cs  # Legacy (SampleScene)
├── Handlers/
│   ├── GazeHandler.cs             # Gaze aggregation + visualization mode switching
│   ├── PostureHandler.cs          # Head pose sync (PhotonView)
│   ├── MeshHandler.cs             # Shared mesh, controller + QR calibration
│   └── ConnectionHandler.cs       # Expert camera, follow/teleport
├── Input/
│   ├── MetaXRGazeInput.cs         # OVR eye tracking (Worker)
│   ├── MetaXRPostureInput.cs      # OVR head pose (Worker)
│   └── OscGazeInput.cs            # /gaze over OSC from Python (Expert)
├── OSC/
│   └── OscSessionManager.cs       # Unity↔Python experiment OSC (ping/ack/calibration/face metrics)
├── QR/
│   └── QRSpatialManager.cs        # MRUK QR detection (Worker) → broadcast marker pose
├── Tasks/
│   ├── IdentificationTask.cs      # QR scan + grip-to-confirm
│   └── AssemblyTask.cs            # Assembly step controller
├── Questionnaire/
│   └── QuestionnaireManager.cs    # NASA-TLX
├── VideoStream/
│   ├── WebRtcVideoSession.cs      # Pure WebRTC peer connection (offer/answer/ICE, reconnect)
│   ├── WebRtcPumpHost.cs          # Persistent WebRTC.Update() pump
│   ├── WorkerVideoStream.cs       # Captures Quest camera → WebRTC (offerer)
│   └── ExpertVideoDisplay.cs      # Displays Worker video (answerer)
├── Visualizers/
│   ├── GazeVisualizer.cs          # Ray / Circle / Frustum / None
│   ├── RayVisualizer.cs / CircleVisualizer.cs / FrustumVisualizer.cs / MockGazeVisualizer.cs
├── Logging/
│   ├── FileLogger.cs              # Thread-safe file logging
│   └── UnityLogCapture.cs         # Routes Debug.Log to the file log
├── UI/                            # CoGazeStrings (localized worker/expert text)
├── Editor/
│   └── CollisionMeshBaker.cs      # Pre-bakes simplified collision meshes for Quest
└── Replay/
    ├── ReplayManager.cs / ReplayLoader.cs
    ├── ReplayGazeDriver.cs / ReplayHandDriver.cs
    └── ReplayData.cs
```

---

## 🔗 Acknowledgments / 謝辞

- **Eye Tracking / アイトラッキング**: [EyeTrackToOSCData by Fialuxe](https://github.com/Fialuxe/EyeTrackToOSCData)
- **Webcam Gaze Estimation / ウェブカメラ視線推定**: [L2CS-Net](https://github.com/Ahmednull/L2CS-Net) (MIT License) — gaze estimation for the Webcam and WebcamFiltered conditions
- **Voice / 音声**: [Photon Voice 2](https://doc.photonengine.com/voice/current/getting-started/voice-intro) (Opus)
- **Video / 映像**: [Unity WebRTC](https://docs.unity3d.com/Packages/com.unity.webrtc@3.0/manual/index.html) (H.264)
- **Networking / ネットワーク**: [Photon PUN 2](https://www.photonengine.com/pun)
- **Base System / ベースシステム**: [remotexr_client](https://github.com/prasanthsasikumar/remotexr_client) and `localxr_client`

This project is distributed under the MIT License. See [LICENSE](LICENSE).
