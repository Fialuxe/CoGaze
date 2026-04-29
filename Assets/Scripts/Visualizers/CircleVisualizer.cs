using UnityEngine;

/// <summary>
/// 注視点に円を描画するVisualizer。
/// LineRendererで円を動的に生成し、ヒットポイントの法線方向に沿って表示する。
/// </summary>
public class CircleVisualizer : MonoBehaviour
{
    private LineRenderer lineRenderer;

    [Header("Circle Settings")]
    private float circleRadius = 0.05f;
    private int segments = 32;
    private Color circleColor = new Color(1f, 0.4f, 0f, 0.9f); // オレンジ系
    private float lineWidth = 0.003f;

    private void Awake()
    {
        CreateCircleRenderer();
    }

    private void CreateCircleRenderer()
    {
        lineRenderer = gameObject.AddComponent<LineRenderer>();
        lineRenderer.positionCount = segments + 1;
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        lineRenderer.loop = true;
        lineRenderer.useWorldSpace = true;

        // マテリアル設定
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.startColor = circleColor;
        lineRenderer.endColor = circleColor;

        lineRenderer.enabled = false;
    }

    /// <summary>注視点と法線を指定して円を描画する</summary>
    public void UpdateVisualization(Vector3 hitPoint, Vector3 normal)
    {
        if (lineRenderer == null) return;

        lineRenderer.enabled = true;

        // 法線に対して垂直な平面上に円を描画
        Quaternion rotation = Quaternion.LookRotation(normal);
        Vector3 right = rotation * Vector3.right;
        Vector3 up = rotation * Vector3.up;

        // サーフェスから少し浮かせる（Zファイティング回避）
        Vector3 offset = normal * 0.001f;

        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)i / segments * Mathf.PI * 2f;
            Vector3 point = hitPoint + offset
                + right * Mathf.Cos(angle) * circleRadius
                + up * Mathf.Sin(angle) * circleRadius;
            lineRenderer.SetPosition(i, point);
        }
    }

    /// <summary>表示/非表示を切り替える</summary>
    public void SetVisible(bool visible)
    {
        if (lineRenderer != null)
        {
            lineRenderer.enabled = visible;
        }
    }

    private void OnDisable()
    {
        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (lineRenderer != null && lineRenderer.material != null)
        {
            Destroy(lineRenderer.material);
        }
    }
}
