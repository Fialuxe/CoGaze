// Posture (head tracking) input interface.
public interface IPostureInput
{
    UnityEngine.Vector3 Position { get; }
    UnityEngine.Quaternion Rotation { get; }
}
