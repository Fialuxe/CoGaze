// Interface for providing pre-placed mesh transform data.
public interface IMeshProvider
{
    UnityEngine.Vector3 MeshPosition { get; }
    UnityEngine.Quaternion MeshRotation { get; }
    UnityEngine.Vector3 MeshScale { get; }
}
