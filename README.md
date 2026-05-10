# CoGaze

CoGaze is a networked remote-collaboration experiment system that connects a **Worker** on a Meta Quest 3 with a **Remote Expert** on a PC. The Expert's eye gaze is visualized in the Worker's VR view in real time. The system runs a structured 9-condition experiment (3 gaze visualization modes × 3 tracking/noise levels) and records detailed trial data for analysis.

CoGazeは、Meta Quest 3を装着した**作業者（Worker）**と、PCを使う**遠隔専門家（Expert）**を繋ぐリアルタイム遠隔協調実験システムです。専門家の視線が作業者のVRビュー内にリアルタイムで可視化されます。3種の視線可視化モード × 3種のノイズレベルからなる9条件実験を実施し、詳細なトライアルデータを記録します。

---

## 📖 System Overview / システム概要

Built on **Unity** with **Photon PUN 2** for signaling and role-based state sync, plus direct **UDP** streams for low-latency audio and video.

### Roles / 役割

| Role | Platform | Responsibilities |
|------|----------|-----------------|
| **Worker** | Meta Quest 3 (Android) | Wears HMD, performs identification & assembly tasks, sees Expert's gaze, streams video |
| **Expert** | Windows PC | Runs experiment, controls step flow, streams eye gaze from Tobii/webcam via OSC, views Worker video |

Role is auto-detected at runtime:
- If a `RoleBasedBootSystem` component is present, its `SelectedRole` is used.
- Otherwise: **Android build → Worker**, **Standalone build → Expert**.

---

## ⚙️ Prerequisites / 前提条件

### Unity Project
- Unity 2022.3 LTS or later with **Android Build Support** module
- **Photon PUN 2** — configure your App ID in `PhotonServerSettings`
- **Meta XR SDK** — OVRCameraRig, OVRSkeleton, OVR Audio Spatializer
- **Concentus.dll** — pure C# Opus codec; place in `Assets/Plugins/`  
  (NuGet: `Concentus`)
- **Newtonsoft.Json** — included via Unity's `com.unity.nuget.newtonsoft-json` package

### Expert PC
- Windows (x64)
- **Python 32-bit** (e.g. `C:/Python311_32/python.exe`) — for Tobii infrared eye tracking
- **Python 64-bit** (e.g. `C:/Python311/python.exe`) — for webcam / high-noise scripts
- **EyeTrackToOSCData** repository cloned locally  
  See: [EyeTrackToOSCData by Fialuxe](https://github.com/Fialuxe/EyeTrackToOSCData)

### Worker (Quest)
- Meta Quest 3 with developer mode enabled
- `RECORD_AUDIO` permission in `Assets/Plugins/Android/AndroidManifest.xml`

---

## 🚀 Setup & Usage / セットアップと使用方法

### 1. Configure the SceneBootstrapper (Inspector)

Attach `SceneBootstrapper` to a root GameObject in `SampleScene` and fill in the Inspector:

| Field | Description |
|-------|-------------|
| `participantNumber` | Participant ID — determines counterbalanced condition order (block order: `n % 6`, gaze order: `(n/6) % 6`, 36 unique orderings) |
| `pythonExecutable32` | Full path to 32-bit Python (Expert only) |
| `pythonExecutable64` | Full path to 64-bit Python (Expert only) |
| `pythonScriptDirectory` | Root of the EyeTrackToOSCData repository (Expert only) |
| `skipTobiiLaunch` | Check this to skip the 32-bit Tobii script (when Tobii is not connected) |
| `tobiiScriptArgs` | CLI args for Block 0 — Tobii infrared (usually empty) |
| `webcamScriptArgs` | CLI args for Block 1 — Webcam (default: `--weights models/L2CSNet_gaze360.pkl --osc-port 8000`) |
| `highNoiseScriptArgs` | CLI args for Block 2 — High noise (usually empty) |
| `webcamCalibArgs` | Webcam calibration args run before Block 1 (default: `--calibrate --weights models/L2CSNet_gaze360.pkl --osc-port 0`) |

### 2. Build for Quest (Worker)
1. Switch platform to **Android**
2. Set Minimum API Level to Android 10 (API 29+)
3. Verify `RECORD_AUDIO` is in `AndroidManifest.xml`
4. Build and deploy to Meta Quest 3

### 3. Run on PC (Expert)
1. Set `RoleBasedBootSystem → Selected Role` to `Expert` (or build as Standalone)
2. Press Play / run the built executable

### 4. Session start flow
1. Both sides join the Photon room automatically
2. A **microphone check** screen appears:
   - **Expert** — interactive: cycle through devices with ◀/▶, check live VU meter, press **Proceed**
   - **Worker** — auto-confirms after 10 s, or press **A/X** on the controller
3. Full initialization runs after mic check is confirmed
4. Expert presses **Enter** to start the experiment

---

## 🎛 Experiment Design / 実験デザイン

9 conditions: **3 gaze visualization modes** × **3 tracking-method/noise blocks**

| Block | Tracking method | Noise level | Python |
|-------|----------------|-------------|--------|
| 0 | Tobii infrared | noise\_low | 32-bit |
| 1 | Webcam | noise\_mid | 64-bit |
| 2 | High noise | noise\_high | 64-bit |

**Gaze visualization modes**: `Ray` / `Circle` / `Frustum`

Block order and gaze order are independently counterbalanced across 6 permutations each (36 total orderings). Python scripts are launched once per block; webcam block includes an automatic calibration step before execution.

### Step types in `StreamingAssets/instructions.txt`

| Type keyword | Behavior |
|---|---|
| `noise` | White noise playback, auto-ends after `whiteNoiseDurationSeconds` |
| `task` | Identification task, countdown timer (`taskDurationSeconds`) |
| `assembly` | Assembly task — Expert camera follows Worker; Worker video streamed to Expert (`assemblyDurationSeconds`) |
| `alignment` | Expert teleports to Worker's position; Enter to advance |
| `questionnaire` | Freeform gate; Enter to advance |

Each step block is separated by `===`. Use `[remote]` / `[local]` markers within a block to show different text to the Expert and the Worker.

### Expert keyboard controls

| Key | Action |
|-----|--------|
| `Enter` | Start / advance after task or noise / end questionnaire |
| `Delete` | Force-skip current running step |

---

## 🌐 Network Architecture / ネットワーク構成

### Transport layers

| Channel | Protocol | Ports | Direction |
|---------|----------|-------|-----------|
| State / events | Photon PUN 2 | (Photon cloud) | Both ways |
| Video stream (JPEG) | UDP | 9100 | Worker → Expert |
| Audio (Opus) | UDP | 9101 | Worker → Expert |
| Audio (Opus) | UDP | 9102 | Expert → Worker |
| OSC gaze input | UDP | 8000 | Python → Expert |

IP/port exchange uses Photon custom player properties. Each side starts its UDP sender as soon as the other player's properties are available.

### Photon event codes

| Code | Sender | Purpose |
|------|--------|---------|
| 43 | Expert | Experiment state: `(state, stepIndex, totalSteps, stepType, remainingSeconds)` |
| 44 | Worker | Hand bones: 24 joints × 3 floats per hand (Quest only, during Task steps) |
| 0xFF (inside 43) | Worker | Sync request — asks Expert to re-broadcast state |

---

## 🎙 Audio Pipeline / 音声パイプライン

Voice communication uses **Opus (Concentus)** over **UDP**:

- **Codec**: SILK wideband, 16 kHz, 16 kbps VBR
- **Inband FEC**: each packet embeds a backup of the previous frame — single-packet loss recovered without audible artifact
- **PLC (Packet Loss Concealment)**: built into Opus decoder; used for multi-frame gaps
- **Gap recovery logic**: gap=0 normal decode; gap=1 FEC; gap>1: PLC × (gap−1) + FEC × 1 + normal; gap≥20 decoder reset
- **Jitter buffer**: 120 ms target, 400 ms hard-reset threshold; gradual clock-drift correction via 1-sample skip per callback
- **Spatial audio (Worker side)**: starts at `spatialBlend=0` (2D, always audible) until the Expert's `PostureHandler` is found in the scene, then switches to HRTF 3D positioned at the Expert's head — prevents OVR HRTF from attenuating the source at world origin
- **WiFi lock**: `WIFI_MODE_FULL_LOW_LATENCY` acquired on Quest to disable AP power-saving mode (eliminates up to 500 ms packet batching)

**Packet format**: `[seq: 2 bytes LE][Opus payload]`

Both microphone (local) and received (remote) audio are saved as 16-bit mono WAV files.

---

## 📊 Data Logging / データログ

All files are written to `{logBaseDirectory}/P{n}/` (default: `Application.persistentDataPath/logs/P{n}/`).  
Logging runs on the Expert PC only.

| File | Content |
|------|---------|
| `trials.csv` | One row per trial: trial ID, participant, condition index, gaze mode, noise level, step type, step index, start/end timestamps (ms), duration |
| `frames.csv` | 30 fps: trial ID, timestamp, elapsed seconds, gaze vector (x/y/z), Worker head pose (position + quaternion), Expert head pose |
| `replay_{id}.json` | Per-trial replay: metadata + per-frame gaze, head poses, hand bone data (24 joints × 3 floats per hand), voice WAV path + offset |
| `voice_local_{ts}.wav` | Expert's microphone (16-bit, 16 kHz mono) |
| `voice_remote_{ts}.wav` | Worker's voice as received (16-bit, 16 kHz mono) |

---

## ▶️ Replay / リプレイ

Open `ReplayScene` to review recorded trials. `ReplayBootstrapper` initializes the replay system automatically.

- Attach `ReplayBootstrapper` to a root GameObject
- Set `Log Folder` to a `P{n}` directory to auto-load the trial list on scene start
- IMGUI panel provides trial selection and playback controls

---

## 🏗 Architecture / アーキテクチャ

```
Assets/Scripts/
├── Audio/
│   ├── AudioDeviceChecker.cs     # Pre-experiment mic check overlay
│   ├── UdpAudioTransport.cs      # Low-latency UDP audio sender/receiver
│   ├── VoiceCommunicator.cs      # Opus encode/decode, jitter buffer, spatial audio, WAV recording
│   └── TcpAudioTransport.cs      # Legacy TCP transport (superseded by UDP)
├── Core/
│   ├── NetworkManager.cs         # Photon connection and room management
│   ├── RoleManager.cs            # Role constants and Photon property helpers
│   └── SceneBootstrapper/
│       ├── SceneBootstrapper.cs  # Entry point: role detection, XR config, mic check, setup dispatch
│       ├── LocalWorkerSetup.cs   # Quest-side init: OVR, audio/video UDP, WiFi lock
│       └── RemoteExpertSetup.cs  # PC-side init: gaze, audio/video UDP, ExperimentManager
├── Experiment/
│   ├── ExperimentManager.cs      # State machine, Latin Square counterbalancing, Python launcher, timer resync
│   ├── ExperimentLogger.cs       # trials.csv, frames.csv, replay JSON, hand bone recording
│   ├── ExpertUI.cs               # Screen-space overlay for Expert
│   ├── WorkerHUD.cs              # World-space HUD for Worker
│   └── WorkerHandBroadcaster.cs  # Streams OVRSkeleton bone data via Photon event 44 (Quest only)
├── Handlers/
│   ├── GazeHandler.cs            # Gaze data aggregation and visualization mode switching
│   ├── PostureHandler.cs         # Head pose sync via PhotonView transform
│   ├── MeshHandler.cs            # Shared 3D mesh loading and calibration
│   └── ConnectionHandler.cs      # FPS camera, transform sync, follow/teleport (Expert)
├── Input/
│   ├── MetaXRGazeInput.cs        # OVR eye tracking (Worker)
│   ├── MetaXRPostureInput.cs     # OVR head pose (Worker)
│   └── OscGazeInput.cs          # UDP/OSC gaze from Python scripts (Expert)
├── VideoStream/
│   ├── UdpVideoTransport.cs      # UDP JPEG video sender/receiver
│   ├── WorkerVideoStream.cs      # Captures Quest camera, sends JPEG frames
│   └── ExpertVideoDisplay.cs     # Displays Worker video during assembly tasks
├── Visualizers/
│   ├── GazeVisualizer.cs         # Switches between Ray / Circle / Frustum
│   ├── RayVisualizer.cs
│   ├── CircleVisualizer.cs
│   ├── FrustumVisualizer.cs
│   └── MockGazeVisualizer.cs
└── Replay/
    ├── ReplayBootstrapper.cs     # Replay scene entry point
    ├── ReplayManager.cs          # Playback timeline controller
    ├── ReplayLoader.cs           # IMGUI file browser and controls
    ├── ReplayGazeDriver.cs       # Drives gaze visualization from replay data
    ├── ReplayHandDriver.cs       # Drives hand visualization from replay data
    └── ReplayData.cs             # Serialization types for replay JSON
```

---

## 🔗 Acknowledgments / 謝辞

- **Eye Tracking / アイトラッキング**: [EyeTrackToOSCData by Fialuxe](https://github.com/Fialuxe/EyeTrackToOSCData)
- **Webcam Gaze Estimation / ウェブカメラ視線推定**: [L2CS-Net](https://github.com/Ahmednull/L2CS-Net) — used for gaze estimation in Block 1 (webcam) and Block 2 (high-noise) conditions via `webcam_gaze_tracker.py`
- **Opus Audio Codec / 音声コーデック**: [Concentus](https://github.com/lostromb/concentus) — pure C# implementation of the Opus codec (IETF RFC 6716) used for low-latency voice communication. Place `Concentus.dll` in `Assets/Plugins/`.
- **Base System / ベースシステム**: [remotexr_client](https://github.com/prasanthsasikumar/remotexr_client) and `localxr_client`
