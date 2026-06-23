using UnityEngine;
using Photon.Pun;

/// <summary>
/// RemoteExpertの視線データをワールド座標に変換し、
/// SetMode()で指定された方式（Ray / Circle / Frustum）で表示するメインコントローラー。
/// Start()でRay/Circle/FrustumVisualizerをAddComponentする。
///
/// FOV は ExperimentManager の状態に応じて切り替える:
///   - Identification タスク: Expert の PC カメラ FOV (60°)
///   - Assembly タスク: PCA カメラの FOV (streamingFov)
/// </summary>
public enum VisualizationMode
{
    Ray,
    Circle,
    Frustum,
    None    // No gaze visualization (NoGaze condition)
}

public class GazeVisualizer : MonoBehaviour
{
    private RayVisualizer rayVisualizer;
    private CircleVisualizer circleVisualizer;
    private FrustumVisualizer frustumVisualizer;

    private VisualizationMode currentMode = VisualizationMode.Ray;
    private GazeHandler targetGazeHandler;

    // Raycast設定（Quest向け最適化済み）
    private const float MAX_RAY_DISTANCE = 10f;  // 100m→10mに短縮（部屋のスケールで十分）
    private LayerMask raycastMask = ~0;           // Initialize to all; narrowed to SharedMesh layer in Initialize()

    // パフォーマンス最適化用
    private int frameCounter = 0;
    // On Android (Quest) raycast less frequently — MeshCollider BVH traversal is costlier on mobile.
#if UNITY_ANDROID && !UNITY_EDITOR
    private const int RAYCAST_INTERVAL = 6;
#else
    private const int RAYCAST_INTERVAL = 3;
#endif
    private const int FIND_HANDLER_INTERVAL = 30;  // Expert検索は30フレームに1回
    private bool lastHit = false;
    private RaycastHit lastHitInfo;
    private Ray lastRay;
    private Camera cachedCamera;

    // ── FOV 設定 ──────────────────────────────────────────────────────
    private const float EXPERT_CAMERA_FOV = 60f;   // Expert の PC カメラ (ConnectionHandler)
    private float streamingFov = 90f;               // PCA カメラの推定 FOV（Quest 3 left camera）
    private float streamingAspect = 4f / 3f;        // PCA の解像度比 (640x480 = 4:3)
    private bool  isStreamingMode = false;

    // ExperimentManager2 参照（FOV 切替用）
    private ExperimentManager2 expManager;

    /// <summary>各Visualizerを初期化する</summary>
    public void Initialize()
    {
        rayVisualizer = gameObject.AddComponent<RayVisualizer>();
        circleVisualizer = gameObject.AddComponent<CircleVisualizer>();
        frustumVisualizer = gameObject.AddComponent<FrustumVisualizer>();

        // Narrow raycast to SharedMesh's layer only — avoids testing every collider in the scene.
        var sharedMesh = GameObject.Find("SharedMesh");
        if (sharedMesh != null)
            raycastMask = 1 << sharedMesh.layer;

        SetMode(VisualizationMode.Ray);
        Debug.Log("[GazeVisualizer] Initialized with all sub-visualizers.");
    }

    /// <summary>表示モードを切り替える</summary>
    public void SetMode(VisualizationMode mode)
    {
        currentMode = mode;

        if (rayVisualizer != null) rayVisualizer.enabled = (mode == VisualizationMode.Ray);
        if (circleVisualizer != null) circleVisualizer.enabled = (mode == VisualizationMode.Circle);
        if (frustumVisualizer != null) frustumVisualizer.enabled = (mode == VisualizationMode.Frustum);

        Debug.Log($"[GazeVisualizer] Mode changed to: {mode}");
    }

    /// <summary>
    /// ストリーミング中の PCA カメラパラメータを設定する。
    /// Assembly 開始時に ExperimentManager から呼ばれる。
    /// </summary>
    public void SetStreamingCameraParams(float fov, float aspect)
    {
        streamingFov    = fov;
        streamingAspect = aspect;
        Debug.Log($"[GazeVisualizer] Streaming camera params: FOV={fov}, aspect={aspect}");
    }

    /// <summary>
    /// ストリーミングモード (Assembly) ON/OFF を切り替える。
    /// cachedCamera の FOV/aspect を適切に更新する。
    /// </summary>
    public void SetStreamingMode(bool streaming)
    {
        isStreamingMode = streaming;
        if (cachedCamera != null)
        {
            cachedCamera.fieldOfView = streaming ? streamingFov : EXPERT_CAMERA_FOV;
            if (streaming)
                cachedCamera.aspect = streamingAspect;
            else
                cachedCamera.ResetAspect(); // PC スクリーンの実際のアスペクト比に戻す
        }
        Debug.Log($"[GazeVisualizer] Streaming mode: {streaming}, FOV={cachedCamera?.fieldOfView}");
    }

    private void Update()
    {
        frameCounter++;

        // ExperimentManager2 の参照を取得（1回だけ）
        if (expManager == null)
        {
            expManager = FindAnyObjectByType<ExperimentManager2>();
        }

        // ExpertのGazeHandlerを探す（重いので30フレームに1回だけ）
        if (targetGazeHandler == null)
        {
            if (frameCounter % FIND_HANDLER_INTERVAL == 0)
            {
                FindExpertGazeHandler();
            }
            return;
        }

        Vector3 gazeData = targetGazeHandler.CurrentGazeData;
        float x = gazeData.x;
        float y = gazeData.y;
        float blink = gazeData.z;


        // モードの同期
        if (currentMode != targetGazeHandler.CurrentMode)
        {
            SetMode(targetGazeHandler.CurrentMode);
        }

        // blink中は非表示
        if (blink > 0.5f)
        {
            HideAll();
            return;
        }

        // Expertの視線を正確に再現するため、ExpertのTransformを持つダミーカメラを使用する
        if (cachedCamera == null)
        {
            cachedCamera = targetGazeHandler.gameObject.AddComponent<Camera>();
            cachedCamera.enabled = false; // レンダリングはしない

            // 現在のモードに応じて FOV を設定
            if (isStreamingMode)
            {
                cachedCamera.fieldOfView = streamingFov;
                cachedCamera.aspect      = streamingAspect;
            }
            else
            {
                cachedCamera.fieldOfView = EXPERT_CAMERA_FOV;
            }
        }

        // 正規化座標をビューポート座標として使用し、Expertの視点からのレイを計算
        Ray ray = cachedCamera.ViewportPointToRay(new Vector3(x, y, 0));

        // Frustumモードの場合、Raycastは不要（固定長の錐台を描画するだけ）
        if (currentMode == VisualizationMode.Frustum)
        {
            if (frustumVisualizer != null && frustumVisualizer.enabled)
            {
                frustumVisualizer.SetCameraParams(cachedCamera.fieldOfView, cachedCamera.aspect);
                frustumVisualizer.UpdateVisualization(ray.origin, ray.direction, Vector3.zero, false);
            }
            return;
        }

        // RaycastはN フレームに1回だけ実行する（MeshColliderへの衝突判定が非常に重いため）
        if (frameCounter % RAYCAST_INTERVAL == 0)
        {
            lastHit = Physics.Raycast(ray, out lastHitInfo, MAX_RAY_DISTANCE, raycastMask);
            lastRay = ray;
        }

        // 各Visualizerに情報を渡す（キャッシュされた結果を使用）
        switch (currentMode)
        {
            case VisualizationMode.Ray:
                if (rayVisualizer != null && rayVisualizer.enabled)
                {
                    Vector3 rayStart = ray.origin + ray.direction * 0.5f;
                    Vector3 endPoint = lastHit ? lastHitInfo.point : ray.GetPoint(MAX_RAY_DISTANCE);
                    rayVisualizer.UpdateVisualization(rayStart, endPoint);
                }
                break;

            case VisualizationMode.Circle:
                if (circleVisualizer != null && circleVisualizer.enabled)
                {
                    if (lastHit)
                    {
                        circleVisualizer.UpdateVisualization(lastHitInfo.point, lastHitInfo.normal);
                        circleVisualizer.SetVisible(true);
                    }
                    else
                    {
                        circleVisualizer.SetVisible(false);
                    }
                }
                break;
        }
    }

    private void HideAll()
    {
        if (rayVisualizer != null) rayVisualizer.SetVisible(false);
        if (circleVisualizer != null) circleVisualizer.SetVisible(false);
        if (frustumVisualizer != null) frustumVisualizer.SetVisible(false);
    }

    /// <summary>シーン内のExpert側GazeHandlerを検索する</summary>
    private void FindExpertGazeHandler()
    {
        GazeHandler[] handlers = FindObjectsByType<GazeHandler>(FindObjectsSortMode.None);
        foreach (var handler in handlers)
        {
            PhotonView pv = handler.GetComponent<PhotonView>();
            if (pv != null) // !pv.IsMine の制限を解除し、自分自身のGazeHandlerも探せるようにする
            {
                // リモートまたはローカルのプレイヤーのGazeHandlerを取得
                var owner = pv.Owner;
                string role = RoleManager.GetPlayerRole(owner);
                if (role == RoleManager.ROLE_EXPERT || role == "expert")
                {
                    targetGazeHandler = handler;
                    Debug.Log("[GazeVisualizer] Found expert's GazeHandler.");
                    return;
                }
            }
        }
    }
}
