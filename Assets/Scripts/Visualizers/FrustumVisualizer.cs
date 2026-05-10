using UnityEngine;

/// <summary>
/// RemoteExpertの視野を錐台（ピラミッド）メッシュとして表示する。
/// GazeVisualizer から呼ばれる想定。
/// 以前動作していた FrustumViewController のメッシュ生成ロジックを統合。
/// </summary>
public class FrustumVisualizer : MonoBehaviour
{
    [Header("Frustum 形状")]
    private float horizontalFOV = 60f;
    private float verticalFOV = 45f;
    private float nearDistance = 0.5f;

    // ピンクバグを防ぐため Sprites/Default を使用
    private Color frustumColor = new Color(0f, 0.8f, 1f, 0.2f);
    private Color edgeColor = new Color(0f, 0.8f, 1f, 0.7f);

    private GameObject _frustumFace;
    private GameObject _frustumEdge;
    private MeshFilter _faceMeshFilter;
    private Mesh frustumMesh;
    
    private bool isInitialized = false;

    private void Awake()
    {
        BuildFrustumObjects();
    }

    public void SetCameraParams(float fieldOfView, float aspectRatio)
    {
        verticalFOV = fieldOfView;
        horizontalFOV = Camera.VerticalToHorizontalFieldOfView(verticalFOV, aspectRatio);
        meshRebuilt = false;
    }

    private void BuildFrustumObjects()
    {
        if (isInitialized) return;
        
        // 面（半透明塗り）
        _frustumFace = new GameObject("FrustumFace");
        _frustumFace.transform.SetParent(transform);
        _faceMeshFilter = _frustumFace.AddComponent<MeshFilter>();
        MeshRenderer faceRenderer = _frustumFace.AddComponent<MeshRenderer>();
        
        // URPでもピンクにならないようSprites/Defaultを使用
        Material faceMat = new Material(Shader.Find("Sprites/Default"));
        faceMat.color = frustumColor;
        faceRenderer.material = faceMat;
        faceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        faceRenderer.receiveShadows = false;

        // 縁（ワイヤーフレーム風ラインレンダラー）
        _frustumEdge = new GameObject("FrustumEdge");
        _frustumEdge.transform.SetParent(transform);
        
        // Sprites/Default がVRのLineRendererで描画されないことがあるため、UI/Defaultを使用する
        Material edgeMat = new Material(Shader.Find("UI/Default"));
        edgeMat.color = edgeColor;

        for (int i = 0; i < 12; i++)
        {
            GameObject edgeLine = new GameObject($"Edge_{i}");
            edgeLine.transform.SetParent(_frustumEdge.transform);
            LineRenderer lr = edgeLine.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth = 0.015f; // 0.005だと細すぎて見えない可能性があるため太くする
            lr.endWidth = 0.015f;
            lr.material = edgeMat;
            lr.useWorldSpace = false; // 以前動作していたローカル座標モード
        }

        isInitialized = true;
        SetVisible(false);
    }

    private bool meshRebuilt = false;
    private LineRenderer[] cachedLineRenderers;

    public void UpdateVisualization(Vector3 origin, Vector3 direction, Vector3 endPoint, bool hasHit)
    {
        // 遠すぎないよう、固定サイズの錐台にする
        float fixedFrustumLength = 1.3f; 
        nearDistance = 0.05f; // 5cm先から開始

        _frustumFace.transform.position = origin;
        _frustumFace.transform.rotation = Quaternion.LookRotation(direction);
        _frustumEdge.transform.position = origin;
        _frustumEdge.transform.rotation = Quaternion.LookRotation(direction);

        // 重いメッシュ生成処理とLineRendererの座標設定は、最初の1回だけ実行する
        // （ローカル座標系で作られているため、親のTransformを動かすだけで追従する）
        if (!meshRebuilt)
        {
            RebuildMesh(fixedFrustumLength);
            meshRebuilt = true;
        }

        SetVisible(true);
    }

    private void RebuildMesh(float length)
    {
        float halfH_far  = Mathf.Tan(horizontalFOV * 0.5f * Mathf.Deg2Rad) * length;
        float halfV_far  = Mathf.Tan(verticalFOV   * 0.5f * Mathf.Deg2Rad) * length;
        float halfH_near = Mathf.Tan(horizontalFOV * 0.5f * Mathf.Deg2Rad) * nearDistance;
        float halfV_near = Mathf.Tan(verticalFOV   * 0.5f * Mathf.Deg2Rad) * nearDistance;

        Vector3[] verts = {
            new Vector3(-halfH_near, -halfV_near, nearDistance),  // 0 near BL
            new Vector3( halfH_near, -halfV_near, nearDistance),  // 1 near BR
            new Vector3( halfH_near,  halfV_near, nearDistance),  // 2 near TR
            new Vector3(-halfH_near,  halfV_near, nearDistance),  // 3 near TL
            new Vector3(-halfH_far,  -halfV_far,  length),        // 4 far BL
            new Vector3( halfH_far,  -halfV_far,  length),        // 5 far BR
            new Vector3( halfH_far,   halfV_far,  length),        // 6 far TR
            new Vector3(-halfH_far,   halfV_far,  length),        // 7 far TL
        };

        // In replay scene there is no Photon role — default to Expert style (far face only)
        // so the frustum doesn't obscure the observer's view.
        bool isExpert = string.IsNullOrEmpty(RoleManager.LocalRole) ||
                        RoleManager.LocalRole == RoleManager.ROLE_EXPERT;

        int[] tris;
        if (isExpert)
        {
            // Expert本人の場合、遠面（大きい四角形）だけを描画し、視界を塞がないようにする
            tris = new int[] {
                4,5,7, 5,6,7
            };
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

        if (frustumMesh == null)
        {
            frustumMesh = new Mesh { name = "FrustumMesh" };
            _faceMeshFilter.mesh = frustumMesh;
        }
        else
        {
            frustumMesh.Clear();
        }
        
        frustumMesh.vertices = verts;
        frustumMesh.triangles = tris;
        frustumMesh.RecalculateNormals();

        UpdateEdgeLines(verts, isExpert);
    }

    private void UpdateEdgeLines(Vector3[] v, bool isExpert)
    {
        (int a, int b)[] edges = {
            (0,1),(1,2),(2,3),(3,0),     // 近面 0-3
            (4,5),(5,6),(6,7),(7,4),     // 遠面 4-7
            (0,4),(1,5),(2,6),(3,7)      // 側面 8-11
        };

        if (cachedLineRenderers == null)
        {
            cachedLineRenderers = _frustumEdge.GetComponentsInChildren<LineRenderer>();
        }
        
        LineRenderer[] lrs = cachedLineRenderers;
        for (int i = 0; i < Mathf.Min(lrs.Length, edges.Length); i++)
        {
            if (isExpert)
            {
                // Expertは遠面の4辺(4〜7)と、手前から奥へ伸びる側面(8〜11)を表示する（近面0〜3だけ隠す）
                if (i >= 4)
                {
                    lrs[i].enabled = true;
                    lrs[i].SetPosition(0, v[edges[i].a]);
                    lrs[i].SetPosition(1, v[edges[i].b]);
                }
                else
                {
                    lrs[i].enabled = false;
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

    public void SetVisible(bool visible)
    {
        if (_frustumFace != null) _frustumFace.SetActive(visible);
        if (_frustumEdge != null) _frustumEdge.SetActive(visible);
    }

    private void OnDisable() => SetVisible(false);
}
