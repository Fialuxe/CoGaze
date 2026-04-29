using UnityEngine;

/// <summary>
/// Inspector上でロールを切り替え可能にするコンポーネント。
/// SceneBootstrapperがこのコンポーネントを参照してどちらのSetupを起動するか決定する。
/// デバッグ時にビルドターゲットに依存せずロールを切り替えられる。
/// </summary>
public enum AppRole
{
    Worker,
    Expert
}

public class RoleBasedBootSystem : MonoBehaviour
{
    [Header("デバッグ用: Inspector上でロールを選択")]
    [SerializeField] private AppRole selectedRole = AppRole.Worker;

    public AppRole SelectedRole => selectedRole;

    public static RoleBasedBootSystem Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }
}
