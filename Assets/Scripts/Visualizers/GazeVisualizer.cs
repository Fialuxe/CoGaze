using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;

// Converts Expert gaze (x,y,blink) to world-space and drives Ray/Circle/Frustum sub-visualizers; FOV switches per task.
public enum VisualizationMode
{
    Ray,
    Circle,
    Frustum,
    None    // No gaze visualization (NoGaze condition)
}

public class GazeVisualizer : MonoBehaviour
{
    private RayVisualizer _rayVisualizer;
    private CircleVisualizer _circleVisualizer;
    private FrustumVisualizer _frustumVisualizer;

    private VisualizationMode _currentMode;
    private GazeHandler _targetGazeHandler;

    // Raycast設定（Quest向け最適化済み）
    private const float k_maxRayDistance = 10f;  // 100m→10mに短縮（部屋のスケールで十分）
    private LayerMask _raycastMask = ~0;          // Initialize to all; narrowed to SharedMesh layer in Initialize()

    // パフォーマンス最適化用
    private int _frameCounter;
    // On Android (Quest) raycast less frequently — MeshCollider BVH traversal is costlier on mobile.
#if UNITY_ANDROID && !UNITY_EDITOR
    private const int k_raycastInterval = 6;
#else
    private const int k_raycastInterval = 3;
#endif
    private const int k_findHandlerInterval = 30;  // Expert検索は30フレームに1回
    private bool _lastHit;
    private RaycastHit _lastHitInfo;
    private Ray _lastRay;
    private Camera _cachedCamera;

    // ── Frustum 移動平均バッファ ──────────────────────────────────────────
    // 90サンプル ≈ 1.5秒 @ 60Hz。ランダムノイズは平均で打ち消し、実注視傾向は残る。
    private const int k_frustumBufferSize = 90;
    private readonly Queue<Vector2> _frustumGazeBuffer = new Queue<Vector2>();
    private Vector2 _frustumGazeSum = Vector2.zero;

    // ── FOV 設定 ──────────────────────────────────────────────────────
    private const float k_expertCameraFov = 60f;   // Expert の PC カメラ (ConnectionHandler)
    // Identification 時の Expert PC レンダリングのアスペクト比。
    // ProjectSettings の defaultScreenWidth/Height = 1920x1080 かつ fullscreenMode=1 のため 16:9。
    // 再構成カメラは Worker(Quest) 上で生成されるので、明示しないと Quest ディスプレイの比率が
    // 使われてしまい、水平方向の視線座標 (横方向のレイ角度) がずれる。
    private const float k_expertCameraAspect = 16f / 9f;
    private float _streamingFov = 90f;               // PCA カメラの推定 FOV（Quest 3 left camera）
    private float _streamingAspect = 4f / 3f;        // PCA の解像度比 (640x480 = 4:3)
    // GazeFix scene only (Fix 2): principal-point deviation from the image centre, expressed as a
    // viewport-space offset added before ViewportPointToRay. Zero when real intrinsics are unknown.
    private Vector2 _streamingPpOffset = Vector2.zero;
    // GazeFix scene only (Fix 3): dedicated ray-reconstruction camera posed from the Worker-local
    // PCA camera pose. The legacy _cachedCamera sits on the Photon-synced RemoteExpert object,
    // which during Assembly is the Worker's own head pose round-tripped through Photon twice
    // (Worker→Expert follow→Worker) — laggy, and offset from the left passthrough camera.
    private Camera _pcaPoseCamera;
    private bool _isStreamingMode;
    private bool _isGazeFallback;
    private int  _fallbackWarnCounter;
    public bool IsGazeAvailable { get; private set; }
    public bool IsGazeFallback   { get; private set; }

    public void Initialize()
    {
        _rayVisualizer = gameObject.AddComponent<RayVisualizer>();
        _circleVisualizer = gameObject.AddComponent<CircleVisualizer>();
        _frustumVisualizer = gameObject.AddComponent<FrustumVisualizer>();

        // Narrow raycast to SharedMesh's layer only — avoids testing every collider in the scene.
        var sharedMesh = GameObject.Find("SharedMesh");
        if (sharedMesh != null)
            _raycastMask = 1 << sharedMesh.layer;

        SetMode(VisualizationMode.Ray);
        Debug.Log("[GazeVisualizer] Initialized with all sub-visualizers.");
    }

    public void SetMode(VisualizationMode mode)
    {
        _currentMode = mode;

        if (_rayVisualizer != null) _rayVisualizer.enabled = (mode == VisualizationMode.Ray);
        if (_circleVisualizer != null) _circleVisualizer.enabled = (mode == VisualizationMode.Circle);
        if (_frustumVisualizer != null) _frustumVisualizer.enabled = (mode == VisualizationMode.Frustum);

        // Frustumモードに入るたびにバッファをリセット（前条件の視線履歴を持ち込まない）
        if (mode == VisualizationMode.Frustum)
        {
            _frustumGazeBuffer.Clear();
            _frustumGazeSum = Vector2.zero;
        }

        Debug.Log($"[GazeVisualizer] Mode changed to: {mode}");
    }

    public void SetStreamingCameraParams(float fov, float aspect)
    {
        _streamingFov    = fov;
        _streamingAspect = aspect;
        // GazeFix scene: real intrinsics arrive from WorkerVideoStream after the PCA starts,
        // i.e. possibly after SetStreamingMode(true) already configured the camera — apply now.
        if (_isStreamingMode && _cachedCamera != null)
        {
            _cachedCamera.fieldOfView = fov;
            _cachedCamera.aspect      = aspect;
        }
        Debug.Log($"[GazeVisualizer] Streaming camera params: FOV={fov}, aspect={aspect}");
    }

    // GazeFix scene only (Fix 2): see _streamingPpOffset.
    public void SetStreamingPrincipalPointOffset(Vector2 viewportOffset)
    {
        _streamingPpOffset = viewportOffset;
        Debug.Log($"[GazeVisualizer] Streaming principal-point viewport offset: {viewportOffset}");
    }

    public void SetStreamingMode(bool streaming)
    {
        _isStreamingMode = streaming;
        if (_cachedCamera != null)
        {
            _cachedCamera.fieldOfView = streaming ? _streamingFov : k_expertCameraFov;
            if (streaming)
                _cachedCamera.aspect = _streamingAspect;
            else
                _cachedCamera.aspect = k_expertCameraAspect; // Identification: Expert PC の 16:9（Quest 比に戻さない）
        }
        Debug.Log($"[GazeVisualizer] Streaming mode: {streaming}, FOV={_cachedCamera?.fieldOfView}");
    }

    private void Update()
    {
        _frameCounter++;

        // ExpertのGazeHandlerを探す（重いので30フレームに1回だけ）
        if (_targetGazeHandler == null)
        {
            if (_frameCounter % k_findHandlerInterval == 0)
                FindExpertGazeHandler();
            return;
        }

        Vector3 gazeData = _targetGazeHandler.CurrentGazeData;
        float x = gazeData.x;
        float y = gazeData.y;
        float blink = gazeData.z;

        // モードの同期
        if (_currentMode != _targetGazeHandler.CurrentMode)
            SetMode(_targetGazeHandler.CurrentMode);

        // blink中は非表示、blink<0はヘッドセンター代替
        bool fallback = blink < 0f;
        if (!fallback && blink > 0.5f)
        {
            HideAll();
            IsGazeAvailable = false;
            if (_isGazeFallback) { _isGazeFallback = false; IsGazeFallback = false; SetFallbackColor(false); }
            return;
        }

        IsGazeAvailable = !fallback;

        if (fallback != _isGazeFallback)
        {
            _isGazeFallback = fallback;
            IsGazeFallback  = fallback;
            SetFallbackColor(fallback);
        }

        if (fallback)
        {
            x = 0.5f; y = 0.5f;
            _fallbackWarnCounter++;
            if (_fallbackWarnCounter % 300 == 0)
                Debug.LogWarning("[GazeVisualizer] Head-centre fallback active — Python gaze stream not available on Expert.");
        }

        // ── GazeFix scene only (Fix 1): pillarbox x remap ─────────────────────────────
        // Tobii の x は Expert モニタ全体（16:9）に対する正規化値だが、Assembly 中の PCA 映像は
        // 4:3 で中央に pillarbox 表示される（ExpertVideoDisplay の FitInParent）。映像はモニタ幅の
        // streamingAspect/screenAspect (= 0.75) しか占めないため、そのまま 4:3 カメラの viewport 座標
        // として使うと端ほど水平にずれる。ここで画面座標 → 映像ローカル座標へ変換する。
        // （縦は FitInParent が高さ一杯に貼るため 1:1 のまま。）
        var fixCfg = GazeProjectionFixConfig.Instance;
        if (_isStreamingMode && fixCfg != null && fixCfg.remapPillarbox && fixCfg.expertScreenAspect > 0f)
        {
            float videoWidthFrac = _streamingAspect / fixCfg.expertScreenAspect;
            if (videoWidthFrac < 1f)
                x = Mathf.Clamp01((x - (1f - videoWidthFrac) * 0.5f) / videoWidthFrac);
        }

        // Expertの視線を正確に再現するため、ExpertのTransformを持つダミーカメラを使用する
        if (_cachedCamera == null)
        {
            _cachedCamera = _targetGazeHandler.gameObject.AddComponent<Camera>();
            _cachedCamera.enabled = false; // レンダリングはしない

            // 現在のモードに応じて FOV を設定
            if (_isStreamingMode)
            {
                _cachedCamera.fieldOfView = _streamingFov;
                _cachedCamera.aspect      = _streamingAspect;
            }
            else
            {
                _cachedCamera.fieldOfView = k_expertCameraFov;
                _cachedCamera.aspect      = k_expertCameraAspect; // Identification: Expert PC の 16:9 に固定
            }
        }

        // ── GazeFix scene only (Fix 3): Worker ローカルの PCA カメラポーズをレイ原点にする ──
        // 従来経路（Worker頭部 → Photon → Expert追従 → Photon → RemoteExpert）は往復2回分の
        // 遅延・補間が乗り、さらに原点が centerEyeAnchor（両目中心）なのに映像は左パススルー
        // カメラという定常オフセットも持つ。Worker 上ではフレームタイムスタンプ時点の
        // PCA ポーズが直接取れるので、それを使う。取れない場合（Expert側・Editor等）は従来通り。
        Camera rayCamera = _cachedCamera;
        if (_isStreamingMode && fixCfg != null && fixCfg.usePcaPoseOrigin
            && WorkerVideoStream.TryGetPcaCameraPose(out Pose pcaPose))
        {
            if (_pcaPoseCamera == null)
            {
                var camGo = new GameObject("GazeFixPcaCamera");
                _pcaPoseCamera = camGo.AddComponent<Camera>();
                _pcaPoseCamera.enabled = false; // レンダリングはしない
                Debug.Log("[GazeVisualizer] GazeFix: using local PCA camera pose for ray reconstruction.");
            }
            _pcaPoseCamera.fieldOfView = _streamingFov;
            _pcaPoseCamera.aspect      = _streamingAspect;
            _pcaPoseCamera.transform.SetPositionAndRotation(pcaPose.position, pcaPose.rotation);
            rayCamera = _pcaPoseCamera;
        }

        // GazeFix scene only (Fix 2): 実内部パラメータの主点が画像中心からずれている分を
        // viewport オフセットとして補正（未設定時はゼロで従来と同一）。
        Vector2 pp = _isStreamingMode ? _streamingPpOffset : Vector2.zero;

        // 正規化座標をビューポート座標として使用し、Expertの視点からのレイを計算
        Ray ray = rayCamera.ViewportPointToRay(new Vector3(x + pp.x, y + pp.y, 0));

        // Frustumモードの場合、Raycastは不要（固定長の錐台を描画するだけ）
        // 方向には瞬間の視線点(x,y)ではなく、直近 k_frustumBufferSize サンプルの移動平均重心を使用。
        // ウェブカメラノイズはランダムなので平均で打ち消され、実注視傾向だけが残る。
        // Circle/Ray = 瞬間の視線点、Frustum = 最近の注視傾向の領域、という役割分担。
        if (_currentMode == VisualizationMode.Frustum)
        {
            // 有効サンプルをバッファに蓄積（blink中はここに来ない）
            var sample = new Vector2(x, y);
            _frustumGazeSum += sample;
            _frustumGazeBuffer.Enqueue(sample);
            if (_frustumGazeBuffer.Count > k_frustumBufferSize)
                _frustumGazeSum -= _frustumGazeBuffer.Dequeue();

            Vector2 meanGaze = _frustumGazeBuffer.Count > 0
                ? _frustumGazeSum / _frustumGazeBuffer.Count
                : new Vector2(0.5f, 0.5f);

            Ray frustumRay = rayCamera.ViewportPointToRay(new Vector3(meanGaze.x + pp.x, meanGaze.y + pp.y, 0));

            if (_frustumVisualizer != null && _frustumVisualizer.enabled)
            {
                _frustumVisualizer.SetCameraParams(rayCamera.fieldOfView, rayCamera.aspect);
                _frustumVisualizer.UpdateVisualization(frustumRay.origin, frustumRay.direction, Vector3.zero, false);
            }
            return;
        }

        // RaycastはN フレームに1回だけ実行する（MeshColliderへの衝突判定が非常に重いため）
        if (_frameCounter % k_raycastInterval == 0)
        {
            _lastHit = Physics.Raycast(ray, out _lastHitInfo, k_maxRayDistance, _raycastMask);
            _lastRay = ray;
        }

        // 各Visualizerに情報を渡す（キャッシュされた結果を使用）
        switch (_currentMode)
        {
            case VisualizationMode.Ray:
                if (_rayVisualizer != null && _rayVisualizer.enabled)
                {
                    Vector3 rayStart = ray.origin + ray.direction * 0.5f;
                    Vector3 endPoint = _lastHit ? _lastHitInfo.point : ray.GetPoint(k_maxRayDistance);
                    _rayVisualizer.UpdateVisualization(rayStart, endPoint);
                }
                break;

            case VisualizationMode.Circle:
                if (_circleVisualizer != null && _circleVisualizer.enabled)
                {
                    if (_lastHit)
                    {
                        _circleVisualizer.UpdateVisualization(_lastHitInfo.point, _lastHitInfo.normal);
                        _circleVisualizer.SetVisible(true);
                    }
                    else
                    {
                        _circleVisualizer.SetVisible(false);
                    }
                }
                break;
        }
    }

    private void OnDestroy()
    {
        if (_pcaPoseCamera != null)
            Destroy(_pcaPoseCamera.gameObject);
    }

    private void HideAll()
    {
        if (_rayVisualizer != null) _rayVisualizer.SetVisible(false);
        if (_circleVisualizer != null) _circleVisualizer.SetVisible(false);
        if (_frustumVisualizer != null) _frustumVisualizer.SetVisible(false);
    }

    private void SetFallbackColor(bool fallback)
    {
        if (_rayVisualizer      != null) _rayVisualizer.SetFallbackMode(fallback);
        if (_circleVisualizer   != null) _circleVisualizer.SetFallbackMode(fallback);
        if (_frustumVisualizer  != null) _frustumVisualizer.SetFallbackMode(fallback);
    }

    private void FindExpertGazeHandler()
    {
        GazeHandler[] handlers = FindObjectsByType<GazeHandler>(FindObjectsSortMode.None);
        foreach (var handler in handlers)
        {
            PhotonView pv = handler.GetComponent<PhotonView>();
            if (pv != null) // !pv.IsMine の制限を解除し、自分自身のGazeHandlerも探せるようにする
            {
                var owner = pv.Owner;
                string role = RoleManager.GetPlayerRole(owner);
                if (role == RoleManager.ROLE_EXPERT || role == "expert")
                {
                    _targetGazeHandler = handler;
                    Debug.Log("[GazeVisualizer] Found expert's GazeHandler.");
                    return;
                }
            }
        }
    }
}
