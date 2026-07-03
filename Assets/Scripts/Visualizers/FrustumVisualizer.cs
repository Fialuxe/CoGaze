using UnityEngine;

// Frustum gaze visualizer: pyramid mesh showing Expert FOV; far face + edge lines, identical on Expert and Worker.
public class FrustumVisualizer : MonoBehaviour
{
    // ピンクバグを防ぐため Sprites/Default を使用
    private static readonly Color k_frustumColor = new Color(0f, 0.8f, 1f, 0.2f);
    private static readonly Color k_edgeColor    = new Color(0f, 0.8f, 1f, 0.7f);

    private const float k_frustumLength = 1.3f;
    private const float k_nearDistance  = 0.05f;
    private const float k_edgeWidth     = 0.015f;

    private float _horizontalFov = 60f;
    private float _verticalFov   = 45f;

    private GameObject     _frustumFace;
    private GameObject     _frustumEdge;
    private MeshFilter     _faceMeshFilter;
    private Mesh           _frustumMesh;
    private LineRenderer[] _cachedLineRenderers;
    private Material       _faceMaterial;
    private Material       _edgeMaterial;
    private bool           _isInitialized;
    private bool           _meshRebuilt;

    private void Awake()
    {
        BuildFrustumObjects();
    }

    private void OnDisable() => SetVisible(false);

    public void SetCameraParams(float fieldOfView, float aspectRatio)
    {
        _verticalFov   = fieldOfView;
        _horizontalFov = Camera.VerticalToHorizontalFieldOfView(_verticalFov, aspectRatio);
        _meshRebuilt   = false;
    }

    public void UpdateVisualization(Vector3 origin, Vector3 direction, Vector3 endPoint, bool hasHit)
    {
        _frustumFace.transform.position = origin;
        _frustumFace.transform.rotation = Quaternion.LookRotation(direction);
        _frustumEdge.transform.position = origin;
        _frustumEdge.transform.rotation = Quaternion.LookRotation(direction);

        // 重いメッシュ生成処理とLineRendererの座標設定は、最初の1回だけ実行する
        // （ローカル座標系で作られているため、親のTransformを動かすだけで追従する）
        if (!_meshRebuilt)
        {
            RebuildMesh();
            _meshRebuilt = true;
        }

        SetVisible(true);
    }

    public void SetVisible(bool visible)
    {
        if (_frustumFace != null) _frustumFace.SetActive(visible);
        if (_frustumEdge != null) _frustumEdge.SetActive(visible);
    }

    public void SetFallbackMode(bool fallback)
    {
        if (_faceMaterial != null)
            _faceMaterial.color = fallback ? new Color(0.5f, 0.5f, 0.5f, 0.15f) : k_frustumColor;
        if (_edgeMaterial != null)
            _edgeMaterial.color = fallback ? new Color(0.7f, 0.7f, 0.7f, 0.5f) : k_edgeColor;
    }

    private void BuildFrustumObjects()
    {
        if (_isInitialized) return;

        // 面（半透明塗り）
        _frustumFace = new GameObject("FrustumFace");
        _frustumFace.transform.SetParent(transform);
        _faceMeshFilter = _frustumFace.AddComponent<MeshFilter>();
        MeshRenderer faceRenderer = _frustumFace.AddComponent<MeshRenderer>();

        // URPでもピンクにならないようSprites/Defaultを使用
        Material faceMat = new Material(Shader.Find("Sprites/Default"));
        faceMat.color = k_frustumColor;
        faceRenderer.material = faceMat;
        _faceMaterial = faceMat;
        faceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        faceRenderer.receiveShadows = false;

        // 縁（ワイヤーフレーム風ラインレンダラー）
        _frustumEdge = new GameObject("FrustumEdge");
        _frustumEdge.transform.SetParent(transform);

        // Sprites/Default がVRのLineRendererで描画されないことがあるため、UI/Defaultを使用する
        Material edgeMat = new Material(Shader.Find("UI/Default"));
        edgeMat.color = k_edgeColor;
        _edgeMaterial = edgeMat;

        for (int i = 0; i < 12; i++)
        {
            GameObject edgeLine = new GameObject($"Edge_{i}");
            edgeLine.transform.SetParent(_frustumEdge.transform);
            LineRenderer lr = edgeLine.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth    = k_edgeWidth;
            lr.endWidth      = k_edgeWidth;
            lr.material      = edgeMat;
            lr.useWorldSpace = false; // ローカル座標モード
        }

        _isInitialized = true;
        SetVisible(false);
    }

    private void RebuildMesh()
    {
        float halfH_far  = Mathf.Tan(_horizontalFov * 0.5f * Mathf.Deg2Rad) * k_frustumLength;
        float halfV_far  = Mathf.Tan(_verticalFov   * 0.5f * Mathf.Deg2Rad) * k_frustumLength;
        float halfH_near = Mathf.Tan(_horizontalFov * 0.5f * Mathf.Deg2Rad) * k_nearDistance;
        float halfV_near = Mathf.Tan(_verticalFov   * 0.5f * Mathf.Deg2Rad) * k_nearDistance;

        Vector3[] verts = {
            new Vector3(-halfH_near, -halfV_near, k_nearDistance),  // 0 near BL
            new Vector3( halfH_near, -halfV_near, k_nearDistance),  // 1 near BR
            new Vector3( halfH_near,  halfV_near, k_nearDistance),  // 2 near TR
            new Vector3(-halfH_near,  halfV_near, k_nearDistance),  // 3 near TL
            new Vector3(-halfH_far,  -halfV_far,  k_frustumLength), // 4 far BL
            new Vector3( halfH_far,  -halfV_far,  k_frustumLength), // 5 far BR
            new Vector3( halfH_far,   halfV_far,  k_frustumLength), // 6 far TR
            new Vector3(-halfH_far,   halfV_far,  k_frustumLength), // 7 far TL
        };

        // 面は Expert/Worker とも遠面のみ。錐台の頂点は（Assembly 中は PCA ポーズ経由で）
        // Worker 自身の頭に一致するため、側面を張ると視界周辺を半透明の壁が囲み作業を妨げる。
        // また役割で形状を変えると Expert と Worker で見えているものが異なり、システム紹介
        // 画像で両者のスクリーンショットが食い違う。縁は UpdateEdgeLines で遠面4辺+側辺4本
        // のみ描き、方向（錐台の広がり）は側辺のラインだけで伝える。
        int[] tris = { 4,5,7, 5,6,7 };

        if (_frustumMesh == null)
        {
            _frustumMesh = new Mesh { name = "FrustumMesh" };
            _faceMeshFilter.mesh = _frustumMesh;
        }
        else
        {
            _frustumMesh.Clear();
        }

        _frustumMesh.vertices  = verts;
        _frustumMesh.triangles = tris;
        _frustumMesh.RecalculateNormals();

        UpdateEdgeLines(verts);
    }

    private void UpdateEdgeLines(Vector3[] v)
    {
        (int a, int b)[] edges = {
            (0,1),(1,2),(2,3),(3,0),     // 近面 0-3
            (4,5),(5,6),(6,7),(7,4),     // 遠面 4-7
            (0,4),(1,5),(2,6),(3,7)      // 側面 8-11
        };

        if (_cachedLineRenderers == null)
            _cachedLineRenderers = _frustumEdge.GetComponentsInChildren<LineRenderer>();

        LineRenderer[] lrs = _cachedLineRenderers;
        for (int i = 0; i < Mathf.Min(lrs.Length, edges.Length); i++)
        {
            // 遠面の4辺(4〜7)と手前から奥へ伸びる側辺(8〜11)のみ表示。近面の縁(0〜3)は
            // 頂点=頭から5cmの位置に来て目の前を横切るため、どちらの役割でも描かない。
            bool show = i >= 4;
            lrs[i].enabled = show;
            if (show)
            {
                lrs[i].SetPosition(0, v[edges[i].a]);
                lrs[i].SetPosition(1, v[edges[i].b]);
            }
        }
    }
}
