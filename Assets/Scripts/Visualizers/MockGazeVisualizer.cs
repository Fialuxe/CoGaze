using UnityEngine;

/// <summary>
/// Visualizerのデバッグ用モック。Photon不要で単体テスト可能。
/// マウス位置を視線データ (x, y) として使い、クリックでblink。
/// 
/// 使い方: 空のGameObjectにアタッチしてPlay。
///         1/2/3キーでRay/Circle/Frustumモード切替。
/// </summary>
public class MockGazeVisualizer : MonoBehaviour
{
    private RayVisualizer rayVisualizer;
    private CircleVisualizer circleVisualizer;
    private FrustumVisualizer frustumVisualizer;

    private VisualizationMode currentMode = VisualizationMode.Ray;
    private const float MAX_RAY_DISTANCE = 100f;

    private void Start()
    {
        rayVisualizer = gameObject.AddComponent<RayVisualizer>();
        circleVisualizer = gameObject.AddComponent<CircleVisualizer>();
        frustumVisualizer = gameObject.AddComponent<FrustumVisualizer>();

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
        bool hit = Physics.Raycast(ray, out RaycastHit hitInfo, MAX_RAY_DISTANCE);

        switch (currentMode)
        {
            case VisualizationMode.Ray:
                Vector3 rayStart = ray.origin + ray.direction * 0.5f;
                Vector3 rayEnd = hit ? hitInfo.point : ray.GetPoint(MAX_RAY_DISTANCE);
                rayVisualizer.UpdateVisualization(rayStart, rayEnd);
                break;

            case VisualizationMode.Circle:
                if (hit)
                {
                    circleVisualizer.UpdateVisualization(hitInfo.point, hitInfo.normal);
                    circleVisualizer.SetVisible(true);
                }
                else
                {
                    circleVisualizer.SetVisible(false);
                }
                break;

            case VisualizationMode.Frustum:
                frustumVisualizer.SetCameraParams(cam.fieldOfView, cam.aspect);
                Vector3 frustumEnd = hit ? hitInfo.point : ray.GetPoint(MAX_RAY_DISTANCE);
                frustumVisualizer.UpdateVisualization(ray.origin, ray.direction, frustumEnd, hit);
                break;
        }
    }

    private void SetMode(VisualizationMode mode)
    {
        currentMode = mode;
        rayVisualizer.enabled = (mode == VisualizationMode.Ray);
        circleVisualizer.enabled = (mode == VisualizationMode.Circle);
        frustumVisualizer.enabled = (mode == VisualizationMode.Frustum);
        Debug.Log($"[MockGazeVisualizer] Mode: {mode}");
    }

    private void HideAll()
    {
        rayVisualizer.SetVisible(false);
        circleVisualizer.SetVisible(false);
        frustumVisualizer.SetVisible(false);
    }
}
