using UnityEngine;

// Circle gaze visualizer: LineRenderer ring aligned to the hit-point normal.
public class CircleVisualizer : MonoBehaviour
{
    private const float k_circleRadius = 0.05f;
    private const int k_segments = 32;
    private const float k_lineWidth = 0.003f;

    private static readonly Color k_circleColor = new Color(1f, 0.4f, 0f, 0.9f);

    private LineRenderer _lineRenderer;

    private void Awake()
    {
        CreateCircleRenderer();
    }

    private void OnDisable()
    {
        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (_lineRenderer != null && _lineRenderer.material != null)
            Destroy(_lineRenderer.material);
    }

    public void UpdateVisualization(Vector3 hitPoint, Vector3 normal)
    {
        if (_lineRenderer == null) return;

        _lineRenderer.enabled = true;

        Quaternion rotation = Quaternion.LookRotation(normal);
        Vector3 right = rotation * Vector3.right;
        Vector3 up = rotation * Vector3.up;

        // サーフェスから少し浮かせる（Zファイティング回避）
        Vector3 offset = normal * 0.001f;

        for (int i = 0; i <= k_segments; i++)
        {
            float angle = (float)i / k_segments * Mathf.PI * 2f;
            Vector3 point = hitPoint + offset
                + right * Mathf.Cos(angle) * k_circleRadius
                + up * Mathf.Sin(angle) * k_circleRadius;
            _lineRenderer.SetPosition(i, point);
        }
    }

    public void SetVisible(bool visible)
    {
        if (_lineRenderer != null)
            _lineRenderer.enabled = visible;
    }

    public void SetFallbackMode(bool fallback)
    {
        if (_lineRenderer == null || _lineRenderer.material == null) return;
        _lineRenderer.material.color = fallback ? new Color(0.7f, 0.7f, 0.7f, 0.5f) : k_circleColor;
    }

    private void CreateCircleRenderer()
    {
        _lineRenderer = gameObject.AddComponent<LineRenderer>();
        _lineRenderer.positionCount = k_segments + 1;
        _lineRenderer.startWidth = k_lineWidth;
        _lineRenderer.endWidth = k_lineWidth;
        _lineRenderer.loop = true;
        _lineRenderer.useWorldSpace = true;
        // Sprites/Default が VR の LineRenderer で描画されないことがあるため UI/Default を使用する（FrustumVisualizer 参照）
        _lineRenderer.material = new Material(Shader.Find("UI/Default"));
        // 色はマテリアル側のみに持たせる。UI/Default は頂点色×マテリアル色を乗算するため、
        // start/endColor にも同色を入れると二重乗算になり (1,0.4,0,0.9)² ≈ (1,0.16,0,0.81) と
        // 設計値より暗く・薄く表示されてしまう（Frustum の縁と同じ持ち方に統一）。
        _lineRenderer.material.color = k_circleColor;
        _lineRenderer.startColor = Color.white;
        _lineRenderer.endColor = Color.white;
        _lineRenderer.enabled = false;
    }
}
