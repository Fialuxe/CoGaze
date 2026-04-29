using UnityEngine;

/// <summary>
/// 視線をレイとして描画するVisualizer。
/// 線分（LineRenderer）ではなく面積（体積）を持つ3Dシリンダーを使用し、
/// 真正面から見ても「点」にならず視認できるようにする。
/// 先端のヒットマーカーも3D球体を使用する。
/// </summary>
public class RayVisualizer : MonoBehaviour
{
    private GameObject rayCylinder;
    private GameObject hitSphere;
    private Material visualMaterial;
    private Material markerMaterial;

    private Color rayColor = new Color(0f, 0.8f, 1f, 0.8f);
    private float rayRadius = 0.008f; // シリンダーの太さ
    private float markerRadius = 0.03f; // マーカーの大きさ

    private void Awake()
    {
        // 半透明UnlitマテリアルとしてSprites/Defaultを利用
        visualMaterial = new Material(Shader.Find("Sprites/Default"));
        visualMaterial.color = rayColor;

        markerMaterial = new Material(Shader.Find("Sprites/Default"));
        markerMaterial.color = new Color(1f, 1f, 1f, 0.9f);

        CreateGeometry();
    }

    private void CreateGeometry()
    {
        // レイ本体（シリンダー）
        rayCylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        rayCylinder.name = "RayCylinder";
        rayCylinder.transform.SetParent(transform);
        Destroy(rayCylinder.GetComponent<Collider>()); // コライダー不要
        rayCylinder.GetComponent<Renderer>().material = visualMaterial;
        rayCylinder.SetActive(false);

        // 先端マーカー（スフィア）
        hitSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        hitSphere.name = "RayHitMarker";
        hitSphere.transform.SetParent(transform);
        Destroy(hitSphere.GetComponent<Collider>()); // コライダー不要
        hitSphere.GetComponent<Renderer>().material = markerMaterial;
        hitSphere.SetActive(false);
    }

    public void UpdateVisualization(Vector3 origin, Vector3 endPoint)
    {
        float distance = Vector3.Distance(origin, endPoint);
        if (distance < 0.01f)
        {
            SetVisible(false);
            return;
        }

        Vector3 dir = (endPoint - origin).normalized;

        rayCylinder.SetActive(true);
        // Cylinderは初期状態で高さ2のY軸方向。なのでYスケールを距離の半分にする。
        rayCylinder.transform.localScale = new Vector3(rayRadius, distance * 0.5f, rayRadius);
        // 中心位置は始点と終点の中間
        rayCylinder.transform.position = origin + dir * (distance * 0.5f);
        // 向きはZ軸(forward)をY軸(up)に向けるように回転
        rayCylinder.transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(90f, 0f, 0f);

        hitSphere.SetActive(true);
        hitSphere.transform.position = endPoint;
        hitSphere.transform.localScale = Vector3.one * (markerRadius * 2f);
    }

    public void SetVisible(bool visible)
    {
        if (rayCylinder != null) rayCylinder.SetActive(visible);
        if (hitSphere != null) hitSphere.SetActive(visible);
    }

    private void OnDisable() => SetVisible(false);

    private void OnDestroy()
    {
        if (visualMaterial != null) Destroy(visualMaterial);
        if (markerMaterial != null) Destroy(markerMaterial);
    }
}
