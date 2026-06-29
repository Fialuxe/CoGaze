using UnityEngine;
using UnityEngine.InputSystem;

// Expert (PC) FPS camera: WASD + mouse; follows followTarget during Assembly; keyboard shortcuts always active.
public class ConnectionHandler : MonoBehaviour
{
    private const float k_moveSpeed        = 5f;
    private const float k_sprintMultiplier = 2f;
    private const float k_mouseSensitivity = 0.1f;
    private const float k_pitchLimit       = 89f;

    private Camera _cam;
    private float _pitch;
    private float _yaw;
    private bool _cursorLocked = true;

    // ── Follow mode (Assembly中のWorker追従) ──────────────────────────
    private Transform _followTarget;

    public bool LockGazeModeKeys;

    private ExperimentManager2              _expMgr;
    private System.Action<ExperimentState>  _onStateChanged;

    private void Start()
    {
        // Lock the manual gaze-mode keys (1/2/3) for the WHOLE experiment run. They are an Idle/Setup
        // debug affordance only — during the run the gaze mode is authoritative from the condition
        // table, so a stray 1/2/3 in a ConditionStart / interval / questionnaire gate would silently
        // overwrite the next condition's gaze format (a hard-to-notice contamination). Unlock only in
        // Idle/Setup (was: unlocked everywhere except TaskRunning).
        _expMgr = FindAnyObjectByType<ExperimentManager2>();
        if (_expMgr != null)
        {
            _onStateChanged = state =>
            {
                bool uiState = state == ExperimentState.Idle || state == ExperimentState.Setup;
                LockGazeModeKeys = !uiState;
                // Setup/Idle: cursor visible so Expert can click the approve button.
                // All other states: cursor locked for FPS camera movement.
                SetCursorLock(!uiState);
            };
            _expMgr.OnStateChanged += _onStateChanged;
        }

        // カメラを探す: Camera.main → FindObjectOfType → 生成
        _cam = Camera.main;

        if (_cam == null)
            _cam = FindAnyObjectByType<Camera>();

        if (_cam == null)
        {
            GameObject camObj = new GameObject("ExpertCamera");
            _cam = camObj.AddComponent<Camera>();
            _cam.tag = "MainCamera";
            _cam.transform.position = new Vector3(0f, 2f, -3f);
            _cam.transform.LookAt(Vector3.zero);
            Debug.Log("[ConnectionHandler] Created new camera.");
        }

        // OVRCameraRig is destroyed on Expert side — ensure an AudioListener exists
        if (FindAnyObjectByType<AudioListener>() == null)
            _cam.gameObject.AddComponent<AudioListener>();

        Vector3 euler = _cam.transform.eulerAngles;
        _yaw   = euler.y;
        _pitch = euler.x > 180f ? euler.x - 360f : euler.x;

        // Start locked only when NOT in a UI-navigation state; Setup needs cursor for approve button.
        bool startInUiState = _expMgr != null &&
            (_expMgr.CurrentState == ExperimentState.Idle || _expMgr.CurrentState == ExperimentState.Setup);
        SetCursorLock(!startInUiState);
    }

    private void OnDestroy()
    {
        if (_expMgr != null && _onStateChanged != null)
            _expMgr.OnStateChanged -= _onStateChanged;
        SetCursorLock(false);
    }

    private void Update()
    {
        if (_cam == null) return;

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
                    if (keyboard.digit1Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Ray;     Debug.Log("Mode: Ray"); }
                    if (keyboard.digit2Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Circle;  Debug.Log("Mode: Circle"); }
                    if (keyboard.digit3Key.wasPressedThisFrame) { gazeHandler.CurrentMode = VisualizationMode.Frustum; Debug.Log("Mode: Frustum"); }
                }
            }

            // ESCでカーソル解除/再ロック
            if (keyboard.escapeKey.wasPressedThisFrame)
                SetCursorLock(!_cursorLocked);
        }

        // ── Follow mode: Assembly中はWorkerの頭に追従 ─────────────────
        if (_followTarget != null)
        {
            _cam.transform.position = _followTarget.position;
            _cam.transform.rotation = _followTarget.rotation;

            // transform を同期（Worker側から見たExpertの位置）
            transform.position = _cam.transform.position;
            transform.rotation = _cam.transform.rotation;

            // pitch/yaw を更新しておく（follow解除後にスムーズに復帰するため）
            Vector3 euler = _followTarget.rotation.eulerAngles;
            _yaw   = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            return; // WASD/マウスのみスキップ
        }

        // ── Free mode: 通常のFPS操作 ─────────────────────────────────
        if (keyboard == null || mouse == null) return;

        // マウス回転
        if (_cursorLocked)
        {
            Vector2 delta = mouse.delta.ReadValue();
            _yaw   += delta.x * k_mouseSensitivity;
            _pitch -= delta.y * k_mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, -k_pitchLimit, k_pitchLimit);
            _cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // WASD移動
        bool sprint = keyboard.leftShiftKey.isPressed;
        float speed = k_moveSpeed * (sprint ? k_sprintMultiplier : 1f);
        Vector3 move = Vector3.zero;
        if (keyboard.wKey.isPressed)        move += _cam.transform.forward;
        if (keyboard.sKey.isPressed)        move -= _cam.transform.forward;
        if (keyboard.aKey.isPressed)        move -= _cam.transform.right;
        if (keyboard.dKey.isPressed)        move += _cam.transform.right;
        if (keyboard.spaceKey.isPressed)    move += Vector3.up;
        if (keyboard.leftCtrlKey.isPressed) move -= Vector3.up;

        _cam.transform.position += move.normalized * speed * Time.deltaTime;

        // RemoteExpert Prefab の transform をカメラに同期
        // → Worker側から見たExpertの位置が正しくなる
        transform.position = _cam.transform.position;
        transform.rotation = _cam.transform.rotation;
    }

    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        _cam.transform.position = position;
        _cam.transform.rotation = rotation;
        Vector3 euler = rotation.eulerAngles;
        _yaw   = euler.y;
        _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
    }

    public void SetFollowTarget(Transform target)
    {
        _followTarget = target;
        Debug.Log($"[ConnectionHandler] Follow mode ON: {(target != null ? target.name : "null")}");
    }

    public void ClearFollowTarget()
    {
        _followTarget = null;
        Debug.Log("[ConnectionHandler] Follow mode OFF — free movement restored.");
    }

    private void SetCursorLock(bool locked)
    {
        _cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
