using UnityEngine;

// Ray gaze visualizer: 3D cylinder + hit-sphere so it remains visible head-on.
public class RayVisualizer : MonoBehaviour
{
    private const float k_rayRadius = 0.008f;
    private const float k_markerRadius = 0.03f;

    private static readonly Color k_rayColor = new Color(0f, 0.8f, 1f, 0.8f);
    private static readonly Color k_markerColor = new Color(1f, 1f, 1f, 0.9f);

    private GameObject _rayCylinder;
    private GameObject _hitSphere;
    private Material _visualMaterial;
    private Material _markerMaterial;

    private void Awake()
    {
        // 半透明UnlitマテリアルとしてSprites/Defaultを利用（VR環境での不在時は UI/Default にフォールバック）
        var rayShader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        _visualMaterial = new Material(rayShader);
        _visualMaterial.color = k_rayColor;

        var markerShader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        _markerMaterial = new Material(markerShader);
        _markerMaterial.color = k_markerColor;

        CreateGeometry();
    }

    private void OnDisable() => SetVisible(false);

    private void OnDestroy()
    {
        if (_visualMaterial != null) Destroy(_visualMaterial);
        if (_markerMaterial != null) Destroy(_markerMaterial);
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

        _rayCylinder.SetActive(true);
        // Cylinderはデフォルトで高さ2のY軸方向なのでYスケールを距離の半分に設定
        _rayCylinder.transform.localScale = new Vector3(k_rayRadius, distance * 0.5f, k_rayRadius);
        _rayCylinder.transform.position = origin + dir * (distance * 0.5f);
        _rayCylinder.transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(90f, 0f, 0f);

        _hitSphere.SetActive(true);
        _hitSphere.transform.position = endPoint;
        _hitSphere.transform.localScale = Vector3.one * (k_markerRadius * 2f);
    }

    public void SetVisible(bool visible)
    {
        if (_rayCylinder != null) _rayCylinder.SetActive(visible);
        if (_hitSphere != null) _hitSphere.SetActive(visible);
    }

    public void SetFallbackMode(bool fallback)
    {
        var c = fallback ? new Color(0.7f, 0.7f, 0.7f, 0.5f) : k_rayColor;
        if (_visualMaterial != null) _visualMaterial.color = c;
        var cm = fallback ? new Color(0.8f, 0.8f, 0.8f, 0.6f) : k_markerColor;
        if (_markerMaterial != null) _markerMaterial.color = cm;
    }

    private void CreateGeometry()
    {
        _rayCylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _rayCylinder.name = "RayCylinder";
        _rayCylinder.transform.SetParent(transform);
        Destroy(_rayCylinder.GetComponent<Collider>());
        _rayCylinder.GetComponent<Renderer>().material = _visualMaterial;
        _rayCylinder.SetActive(false);

        _hitSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _hitSphere.name = "RayHitMarker";
        _hitSphere.transform.SetParent(transform);
        Destroy(_hitSphere.GetComponent<Collider>());
        _hitSphere.GetComponent<Renderer>().material = _markerMaterial;
        _hitSphere.SetActive(false);
    }
}
