/// <summary>
/// Posture (head tracking) input interface.
/// </summary>
public interface IPostureInput
{
    UnityEngine.Vector3 Position { get; }
    UnityEngine.Quaternion Rotation { get; }
}
