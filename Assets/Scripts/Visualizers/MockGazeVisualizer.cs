using UnityEngine;

// Debug-only visualizer: mouse position → gaze (x,y), click → blink; 1/2/3 keys cycle Ray/Circle/Frustum.
public class MockGazeVisualizer : MonoBehaviour
{
    private RayVisualizer _rayVisualizer;
    private CircleVisualizer _circleVisualizer;
    private FrustumVisualizer _frustumVisualizer;

    private VisualizationMode _currentMode = VisualizationMode.Ray;
    private const float k_maxRayDistance = 100f;

    private void Start()
    {
        _rayVisualizer = gameObject.AddComponent<RayVisualizer>();
        _circleVisualizer = gameObject.AddComponent<CircleVisualizer>();
        _frustumVisualizer = gameObject.AddComponent<FrustumVisualizer>();

        SetMode(VisualizationMode.Ray);
        Debug.Log("[MockGazeVisualizer] Started. Keys: 1=Ray, 2=Circle, 3=Frustum");
    }

    private void Update()
    {
        // モード切替 (1/2/3)
        if (UnityEngine.InputSystem.Keyboard.current.digit1Key.wasPressedThisFrame) SetMode(VisualizationMode.Ray);
        if (UnityEngine.InputSystem.Keyboard.current.digit2Key.wasPressedThisFrame) SetMode(VisualizationMode.Circle);
        if (UnityEngine.InputSystem.Keyboard.current.digit3Key.wasPressedThisFrame) SetMode(VisualizationMode.Frustum);

        // マウス位置 → 正規化座標
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 mousePos = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
        float x = mousePos.x / Screen.width;
        float y = mousePos.y / Screen.height;

        // 左クリック = blink
        bool blink = UnityEngine.InputSystem.Mouse.current.leftButton.isPressed;
        if (blink)
        {
            HideAll();
            return;
        }

        // ビューポート座標からレイ生成
        Ray ray = cam.ViewportPointToRay(new Vector3(x, y, 0));
        bool hit = Physics.Raycast(ray, out RaycastHit hitInfo, k_maxRayDistance);

        switch (_currentMode)
        {
            case VisualizationMode.Ray:
                Vector3 rayStart = ray.origin + ray.direction * 0.5f;
                Vector3 rayEnd = hit ? hitInfo.point : ray.GetPoint(k_maxRayDistance);
                _rayVisualizer.UpdateVisualization(rayStart, rayEnd);
                break;

            case VisualizationMode.Circle:
                if (hit)
                {
                    _circleVisualizer.UpdateVisualization(hitInfo.point, hitInfo.normal);
                    _circleVisualizer.SetVisible(true);
                }
                else
                {
                    _circleVisualizer.SetVisible(false);
                }
                break;

            case VisualizationMode.Frustum:
                _frustumVisualizer.SetCameraParams(cam.fieldOfView, cam.aspect);
                Vector3 frustumEnd = hit ? hitInfo.point : ray.GetPoint(k_maxRayDistance);
                _frustumVisualizer.UpdateVisualization(ray.origin, ray.direction, frustumEnd, hit);
                break;
        }
    }

    private void SetMode(VisualizationMode mode)
    {
        _currentMode = mode;
        _rayVisualizer.enabled = (mode == VisualizationMode.Ray);
        _circleVisualizer.enabled = (mode == VisualizationMode.Circle);
        _frustumVisualizer.enabled = (mode == VisualizationMode.Frustum);
        Debug.Log($"[MockGazeVisualizer] Mode: {mode}");
    }

    private void HideAll()
    {
        _rayVisualizer.SetVisible(false);
        _circleVisualizer.SetVisible(false);
        _frustumVisualizer.SetVisible(false);
    }
}
