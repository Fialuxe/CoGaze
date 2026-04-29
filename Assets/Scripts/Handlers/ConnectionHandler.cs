using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// RemoteExpert (PC) 側のFPSカメラ操作。
/// WASD移動 + マウスで視点回転。Minecraftスタイル。
/// 新しい Input System パッケージ対応。
/// </summary>
public class ConnectionHandler : MonoBehaviour
{
    [Header("Movement")]
    private float moveSpeed = 5f;
    private float sprintMultiplier = 2f;

    [Header("Mouse Look")]
    private float mouseSensitivity = 0.1f;
    private float pitchLimit = 89f;

    private Camera cam;
    private float pitch = 0f;
    private float yaw = 0f;
    private bool cursorLocked = true;

    private void Start()
    {
        // カメラを探す: Camera.main → FindObjectOfType → 生成
        cam = Camera.main;

        if (cam == null)
        {
            cam = FindAnyObjectByType<Camera>();
        }

        if (cam == null)
        {
            GameObject camObj = new GameObject("ExpertCamera");
            cam = camObj.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0f, 2f, -3f);
            cam.transform.LookAt(Vector3.zero);
            Debug.Log("[ConnectionHandler] Created new camera.");
        }

        // 現在の向きを初期値にする
        Vector3 euler = cam.transform.eulerAngles;
        yaw = euler.y;
        pitch = euler.x > 180f ? euler.x - 360f : euler.x;

        SetCursorLock(true);
    }

    private void Update()
    {
        if (cam == null) return;

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        // Gaze Mode の切り替え（1, 2, 3キー）
        var gazeHandler = GetComponent<GazeHandler>();
        if (gazeHandler != null)
        {
            if (keyboard.digit1Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Ray; Debug.Log("Mode: Ray"); }
            if (keyboard.digit2Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Circle; Debug.Log("Mode: Circle"); }
            if (keyboard.digit3Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Frustum; Debug.Log("Mode: Frustum"); }
        }

        // ESCでカーソル解除/再ロック
        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            SetCursorLock(!cursorLocked);
        }

        // マウス回転
        if (cursorLocked)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity;
            pitch -= delta.y * mouseSensitivity;
            pitch = Mathf.Clamp(pitch, -pitchLimit, pitchLimit);
            cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        // WASD移動
        bool sprint = keyboard.leftShiftKey.isPressed;
        float speed = moveSpeed * (sprint ? sprintMultiplier : 1f);
        Vector3 move = Vector3.zero;
        if (keyboard.wKey.isPressed) move += cam.transform.forward;
        if (keyboard.sKey.isPressed) move -= cam.transform.forward;
        if (keyboard.aKey.isPressed) move -= cam.transform.right;
        if (keyboard.dKey.isPressed) move += cam.transform.right;
        if (keyboard.spaceKey.isPressed) move += Vector3.up;
        if (keyboard.leftCtrlKey.isPressed) move -= Vector3.up;

        cam.transform.position += move.normalized * speed * Time.deltaTime;

        // RemoteExpert Prefab の transform をカメラに同期
        // → Worker側から見たExpertの位置が正しくなる
        transform.position = cam.transform.position;
        transform.rotation = cam.transform.rotation;
    }

    private void SetCursorLock(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void OnDestroy()
    {
        SetCursorLock(false);
    }
}
