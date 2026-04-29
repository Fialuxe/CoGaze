using UnityEngine;
using Photon.Pun;

/// <summary>
/// 事前配置済みメッシュのTransformをPhoton RPC経由で全プレイヤーに共有するHandler。
/// LocalWorker (Quest) 側ではコントローラーでメッシュのキャリブレーション（位置合わせ）が可能。
///
/// キャリブレーション操作（グリップ押下中のみ有効）:
///   左スティック   — XZ平面移動（高さ変更なし）
///   右スティックX  — Y軸回転
///   Aボタン        — キャリブレーション確定＆送信
/// </summary>
public class MeshHandler : MonoBehaviourPun
{
    [Header("Scene内の事前配置メッシュのオブジェクト名")]
    [SerializeField] private string meshObjectName = "SharedMesh";

    [Header("Calibration Settings")]
    [SerializeField] private float moveSpeed = 1.0f;
    [SerializeField] private float rotateSpeed = 45f;

    private GameObject meshObject;
    private bool isCalibrating = false;

    private void Start()
    {
        meshObject = GameObject.Find(meshObjectName);
        if (meshObject == null)
        {
            Debug.LogWarning(
                $"[MeshHandler] Pre-placed mesh '{meshObjectName}' not found in scene. " +
                "Make sure a GameObject with this name exists.");
            return;
        }

        // ==========================================
        // 自動軽量化処理（Questの負荷を下げるための最適化）
        // ==========================================
        OptimizeMeshPerformance(meshObject);
    }

    private void OptimizeMeshPerformance(GameObject targetMesh)
    {
        // 1. 影の無効化（超ハイポリゴンの影描画はGPUを即死させるため）
        MeshRenderer[] renderers = targetMesh.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var r in renderers)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // 2. MeshColliderの自動付与（コライダーが無いとレイキャストの衝突判定ができないため）
        MeshFilter[] filters = targetMesh.GetComponentsInChildren<MeshFilter>(true);
        int addedColliders = 0;
        foreach (var filter in filters)
        {
            // すでに何らかのコライダーが付いていればスキップ
            if (filter.GetComponent<Collider>() == null)
            {
                filter.gameObject.AddComponent<MeshCollider>();
                addedColliders++;
            }
        }
        if (addedColliders > 0)
        {
            Debug.Log($"[MeshHandler] Automatically added {addedColliders} MeshColliders to the mesh.");
        }

        // 3. 物理エンジン（PhysX）の最適化
        // MeshColliderがついている静的オブジェクトをスクリプトで動かすと、毎フレームBVH（空間ツリー）の再構築が走りCPUが死ぬ。
        // これを防ぐために「物理演算の影響を受けないが、動かすことはできる」Kinematic Rigidbodyを付与する。
        MeshCollider[] colliders = targetMesh.GetComponentsInChildren<MeshCollider>(true);
        if (colliders.Length > 0 && targetMesh.GetComponent<Rigidbody>() == null)
        {
            Rigidbody rb = targetMesh.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            Debug.Log("[MeshHandler] Added Kinematic Rigidbody to prevent physics spikes.");
        }

        Debug.Log($"[MeshHandler] Optimized {renderers.Length} renderers and prepared colliders for '{targetMesh.name}'.");
    }

    private void Update()
    {
        if (!photonView.IsMine || meshObject == null) return;

#if UNITY_ANDROID
        UpdateCalibration();
#endif
    }

#if UNITY_ANDROID
    private void UpdateCalibration()
    {
        // ==========================================
        // 追加機能：メッシュのON/OFF切り替え（左コンのXボタン）
        // ==========================================
        if (OVRInput.GetDown(OVRInput.Button.Three)) // Xボタン
        {
            ToggleMeshVisibility();
        }

        // コントローラー動作テスト用（左コンのYボタン）
        if (OVRInput.GetDown(OVRInput.Button.Four)) // Yボタン
        {
            Debug.Log("✅ [Controller Test] Y Button Pressed! コントローラーの入力は正常にUnityに届いています！");
        }

        // ==========================================
        // 既存：キャリブレーション処理
        // ==========================================
        // 右コントローラーのグリップを握っている間だけキャリブレーションモード
        bool gripHeld = OVRInput.Get(OVRInput.Button.SecondaryHandTrigger);

        if (!gripHeld)
        {
            if (isCalibrating)
            {
                isCalibrating = false;
                Debug.Log("[MeshHandler] Calibration paused (grip released).");
            }
            return;
        }

        if (!isCalibrating)
        {
            isCalibrating = true;
            Debug.Log("[MeshHandler] Calibration active (right grip held).");
        }

        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        bool aHeld = OVRInput.Get(OVRInput.Button.One);
        bool triggerHeld = OVRInput.Get(OVRInput.Button.SecondaryIndexTrigger);

        if (triggerHeld)
        {
            // 人差し指トリガーを押しながらスティック上下 → 高さ（Y軸）移動
            if (Mathf.Abs(stick.y) > 0.1f)
            {
                Vector3 heightMovement = Vector3.up * stick.y * moveSpeed * Time.deltaTime;
                meshObject.transform.position += heightMovement;
            }
        }
        else if (aHeld)
        {
            // A押しながらスティック左右 → Y軸回転
            if (Mathf.Abs(stick.x) > 0.1f)
            {
                meshObject.transform.Rotate(Vector3.up, stick.x * rotateSpeed * Time.deltaTime, Space.World);
            }
        }
        else
        {
            // スティックのみ → XZ平面移動
            if (stick.sqrMagnitude > 0.01f)
            {
                Transform hmd = Camera.main != null ? Camera.main.transform : transform;
                Vector3 forward = hmd.forward;
                forward.y = 0f;
                forward.Normalize();
                Vector3 right = hmd.right;
                right.y = 0f;
                right.Normalize();

                Vector3 movement = (forward * stick.y + right * stick.x) * moveSpeed * Time.deltaTime;
                meshObject.transform.position += movement;
            }
        }

        // Bボタン → 確定＆RPC送信
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            SendMeshTransform();
            Debug.Log("[MeshHandler] Calibration confirmed and sent!");
        }
    }
#endif

    private void ToggleMeshVisibility()
    {
        if (meshObject != null)
        {
            MeshRenderer[] renderers = meshObject.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length > 0)
            {
                // 先頭のレンダラーの状態を反転させる
                bool newState = !renderers[0].enabled;
                foreach (var r in renderers)
                {
                    r.enabled = newState;
                }
                Debug.Log($"[MeshHandler] Mesh visibility toggled to: {(newState ? "ON" : "OFF")}");
            }
        }
    }

    /// <summary>
    /// 現在のメッシュTransformを全プレイヤーに送信する。
    /// </summary>
    public void SendMeshTransform()
    {
        if (meshObject == null)
        {
            Debug.LogWarning("[MeshHandler] No mesh object to send.");
            return;
        }

        photonView.RPC(
            nameof(RPC_ReceiveMeshTransform),
            RpcTarget.AllBuffered,
            meshObject.transform.position,
            meshObject.transform.rotation,
            meshObject.transform.localScale
        );

        Debug.Log("[MeshHandler] Mesh transform sent via RPC.");
    }

    [PunRPC]
    private void RPC_ReceiveMeshTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (meshObject == null)
        {
            meshObject = GameObject.Find(meshObjectName);
        }

        if (meshObject != null)
        {
            meshObject.transform.position = position;
            meshObject.transform.rotation = rotation;
            meshObject.transform.localScale = scale;
            Debug.Log($"[MeshHandler] Mesh transform updated: pos={position}, rot={rotation.eulerAngles}");
        }
        else
        {
            Debug.LogWarning("[MeshHandler] Could not find mesh object to apply received transform.");
        }
    }
}
