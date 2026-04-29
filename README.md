# CoGaze (LocalXR / RemoteXR)

CoGaze is a networked collaborative Mixed Reality system designed to bridge the gap between an on-site worker (Local Worker) in VR/MR and a remote supporter (Remote Expert) on a PC. By synchronizing spatial meshes, head postures, and gaze data, it enables seamless remote assistance where the remote expert can literally "look" and point at objects in the worker's physical space.

CoGazeは、VR/MRデバイスを装着した現場の作業者（Local Worker）と、PCを使用する遠隔の支援者（Remote Expert）を繋ぐ、ネットワーク対応のコラボレーションシステムです。空間メッシュ、頭部の姿勢、そして視線データを同期することで、遠隔地の専門家が現場の空間を「見て」、視線で直接モノを指し示すことができるシームレスな遠隔支援を実現します。

---

## 📖 System Overview (システム概要)

This project is built on **Unity** and uses **Photon PUN 2** for real-time networking. The architecture is strictly role-based to ensure scalable and clean code.

本プロジェクトは **Unity** をベースにし、リアルタイムネットワーク通信に **Photon PUN 2** を使用しています。コードの拡張性と可読性を保つため、完全なロール（役割）ベースのアーキテクチャを採用しています。

### Roles (役割)
1. **Local Worker (Meta Quest)**
   - Wears a Meta Quest headset.
   - Shares physical head movements (Position/Rotation) with the expert.
   - Calibrates the pre-scanned 3D mesh of the room to match the real physical world.
   - Sees the remote expert's gaze visualized in the physical space.
2. **Remote Expert (PC)**
   - Operates from a PC screen.
   - Uses WASD/Mouse to navigate the shared 3D scanned room.
   - Uses an external eye tracker (e.g., Tobii Eye Tracker) via OSC to send gaze data.
   - Can switch between different gaze visualization modes (Ray, Circle, Frustum) to guide the worker.

---

## ⚙️ Current Specifications (現在の仕様)

### 1. Network & Role Bootstrapping
- **Photon PUN 2**: Automatically connects to the Asia region and joins a shared room (`CoGaze_Room`).
- **RoleBasedBootSystem**: The application role (`Worker` or `Expert`) is defined in the Unity Editor before building. The `SceneBootstrapper` automatically routes initialization logic based on this role.

### 2. Local Worker (Quest) Features
- **Meta XR Integration**: Uses `MetaXRPostureInput` to accurately track the HMD.
- **Mesh Calibration (Right Controller)**:
  - **Hold Right Grip**: Enter calibration mode.
  - **Right Stick (Up/Down/Left/Right)**: Move the room mesh along the XZ plane.
  - **Hold Right Index Trigger + Right Stick (Up/Down)**: Adjust the height (Y-axis) of the mesh.
  - **Hold Right A Button + Right Stick (Left/Right)**: Rotate the mesh (Yaw).
- **Performance Optimizations (Left Controller)**:
  - **X Button**: Toggles the heavy mesh visibility ON/OFF to test hardware limits.
  - Automatically strips shadows, adds Kinematic Rigidbodies to static meshes, and drops physics tick rates to maintain >30 FPS on the Quest.
  - Automatically generates `MeshCollider` for imported room meshes if missing.

### 3. Remote Expert (PC) Features
- **FPS Navigation**: Standard PC FPS controls (WASD + Mouse) via `ConnectionHandler` to explore the mesh.
- **OSC Gaze Input**: 
  - Listens on `UDP Port 8000`, address `/gaze`.
  - Expects `[float x, float y, float blink]`.
  - Supports raw Tobii tracker coordinates (Top-Left = `(0,0)`) and automatically converts them to Unity's Viewport coordinates (Bottom-Left = `(0,0)`).
- **Gaze Visualization Modes**:
  - The expert can press `1`, `2`, or `3` to change how their gaze is displayed to the worker:
    - `1`: **Ray** (A highly visible 3D laser pointer)
    - `2`: **Circle** (A reticle on the surface being looked at)
    - `3`: **Frustum** (A camera frustum showing the exact field of view)

---

## 🚀 Setup & Usage (セットアップと使用方法)

### Build for Quest (Local Worker)
1. Open the scene in Unity.
2. Select the GameObject with `RoleBasedBootSystem`.
3. Set **Selected Role** to `Worker`.
4. Build and Run on the Meta Quest.

### Run on PC (Remote Expert)
1. Select the GameObject with `RoleBasedBootSystem`.
2. Set **Selected Role** to `Expert`.
3. Press Play in the Unity Editor (or build for Windows).
4. Run the Python OSC Mock script (`mock_gaze_osc.py`) or the actual Tobii OSC server (`tobii_osc_server.py`) to stream your mouse/eye coordinates to Unity!

---

## 🛠 Architecture Details (アーキテクチャ詳細)

*   `SceneBootstrapper.cs`: The main entry point. Initializes PUN and delegates to `LocalWorkerSetup` or `RemoteExpertSetup`.
*   `PostureHandler.cs`: Synchronizes Transform data between clients.
*   `GazeHandler.cs`: Synchronizes Gaze `(x, y, blink)` and `VisualizationMode` between clients.
*   `MeshHandler.cs`: Handles heavy mesh loading, automatic collider generation, runtime GPU/CPU optimization, and physical calibration.
*   `GazeVisualizer.cs`: Reconstructs the 3D ray accurately using the Expert's perspective and renders the Ray/Circle/Frustum.

---

## 🔗 Acknowledgments & References (謝辞・参考資料)

*   **Eye Tracking**: For details on eye tracking integration using Tobii EyeX and EyeTrax, please refer to [EyeTrackToOSCData by Fialuxe](https://github.com/Fialuxe/EyeTrackToOSCData).
*   **Base System**: This project is developed and expanded based on [remotexr_client](https://github.com/prasanthsasikumar/remotexr_client) and implementation of `localxr_client`.

*   **アイトラッキング**: Tobii EyeX および EyeTrax を用いたアイトラッキング（OSC送信）の詳細については、[EyeTrackToOSCData (Fialuxe)](https://github.com/Fialuxe/EyeTrackToOSCData) を参照してください。
*   **ベースシステム**: 本システムは、[remotexr_client](https://github.com/prasanthsasikumar/remotexr_client) および `localxr_client` の実装をもとに発展させ構築されています。
