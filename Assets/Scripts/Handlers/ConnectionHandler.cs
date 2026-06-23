using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// RemoteExpert (PC) 側のFPSカメラ操作。
/// WASD移動 + マウスで視点回転。Minecraftスタイル。
/// 新しい Input System パッケージ対応。
///
/// followTarget が設定されている場合（Assembly中）、
/// WASD/マウス入力を無視して followTarget の位置・回転に追従する。
/// キーボード入力（Gaze Mode切替、ESC等）は常に処理される。
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

    // ── Follow mode (Assembly中のWorker追従) ──────────────────────────
    private Transform followTarget;

    public bool LockGazeModeKeys = false;

    private ExperimentManager2              _expMgr;
    private System.Action<ExperimentState>  _onStateChanged;

    private void Start()
    {
        // Lock gaze mode keys during a running task
        _expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (_expMgr != null)
        {
            _onStateChanged = state => LockGazeModeKeys = state == ExperimentState.TaskRunning;
            _expMgr.OnStateChanged += _onStateChanged;
        }

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

        // OVRCameraRig is destroyed on Expert side — ensure an AudioListener exists
        if (FindAnyObjectByType<AudioListener>() == null)
            cam.gameObject.AddComponent<AudioListener>();

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

        // ── キーボード入力（常に処理 — follow mode 中も有効） ──────────
        if (keyboard != null)
        {
            // Gaze Mode の切り替え（1, 2, 3キー）— 実験中はロック
            if (!LockGazeModeKeys)
            {
                var gazeHandler = GetComponent<GazeHandler>();
                if (gazeHandler != null)
                {
                    if (keyboard.digit1Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Ray; Debug.Log("Mode: Ray"); }
                    if (keyboard.digit2Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Circle; Debug.Log("Mode: Circle"); }
                    if (keyboard.digit3Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Frustum; Debug.Log("Mode: Frustum"); }
                }
            }

            // ESCでカーソル解除/再ロック
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                SetCursorLock(!cursorLocked);
            }
        }

        // ── Follow mode: Assembly中はWorkerの頭に追従 ─────────────────
        if (followTarget != null)
        {
            cam.transform.position = followTarget.position;
            cam.transform.rotation = followTarget.rotation;

            // transform を同期（Worker側から見たExpertの位置）
            transform.position = cam.transform.position;
            transform.rotation = cam.transform.rotation;

            // pitch/yaw を更新しておく（follow解除後にスムーズに復帰するため）
            Vector3 euler = followTarget.rotation.eulerAngles;
            yaw   = euler.y;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            return; // WASD/マウスのみスキップ
        }

        // ── Free mode: 通常のFPS操作 ─────────────────────────────────
        if (keyboard == null || mouse == null) return;

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

    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        cam.transform.position = position;
        cam.transform.rotation = rotation;
        Vector3 euler = rotation.eulerAngles;
        yaw   = euler.y;
        pitch = euler.x > 180f ? euler.x - 360f : euler.x;
    }

    /// <summary>
    /// Assembly中にWorkerの頭位置に追従するモードを開始する。
    /// followTarget が非null の間、WASD/マウス入力は無視される。
    /// キーボード入力（Gaze Mode切替等）は引き続き処理される。
    /// </summary>
    public void SetFollowTarget(Transform target)
    {
        followTarget = target;
        Debug.Log($"[ConnectionHandler] Follow mode ON: {(target != null ? target.name : "null")}");
    }

    /// <summary>
    /// 追従モードを解除し、通常のFPS操作に戻す。
    /// </summary>
    public void ClearFollowTarget()
    {
        followTarget = null;
        Debug.Log("[ConnectionHandler] Follow mode OFF — free movement restored.");
    }

    private void SetCursorLock(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void OnDestroy()
    {
        if (_expMgr != null && _onStateChanged != null)
            _expMgr.OnStateChanged -= _onStateChanged;
        SetCursorLock(false);
    }
}
