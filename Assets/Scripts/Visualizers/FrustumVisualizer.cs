using UnityEngine;

// Frustum gaze visualizer: pyramid mesh showing Expert FOV; face-only for Expert, full for Worker.
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

        // In replay scene there is no Photon role — default to Expert style (far face only)
        // so the frustum doesn't obscure the observer's view.
        bool isExpert = string.IsNullOrEmpty(RoleManager.LocalRole) ||
                        RoleManager.LocalRole == RoleManager.ROLE_EXPERT;

        int[] tris;
        if (isExpert)
        {
            // Expert本人の場合、遠面（大きい四角形）だけを描画し、視界を塞がないようにする
            tris = new int[] { 4,5,7, 5,6,7 };
        }
        else
        {
            // Workerから見る場合は、遠面と側面を描画する（近面は顔に埋まるので描画しない）
            tris = new int[] {
                4,5,7, 5,6,7,   // 遠面
                0,1,4, 1,5,4,   // 下面
                2,3,6, 3,7,6,   // 上面
                0,4,3, 3,4,7,   // 左面
                1,2,5, 2,6,5    // 右面
            };
        }

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

        UpdateEdgeLines(verts, isExpert);
    }

    private void UpdateEdgeLines(Vector3[] v, bool isExpert)
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
            if (isExpert)
            {
                // Expertは遠面の4辺(4〜7)と、手前から奥へ伸びる側面(8〜11)を表示する（近面0〜3だけ隠す）
                bool show = i >= 4;
                lrs[i].enabled = show;
                if (show)
                {
                    lrs[i].SetPosition(0, v[edges[i].a]);
                    lrs[i].SetPosition(1, v[edges[i].b]);
                }
            }
            else
            {
                // Workerは全エッジ表示
                lrs[i].enabled = true;
                lrs[i].SetPosition(0, v[edges[i].a]);
                lrs[i].SetPosition(1, v[edges[i].b]);
            }
        }
    }
}
