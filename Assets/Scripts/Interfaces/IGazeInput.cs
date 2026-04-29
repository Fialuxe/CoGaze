/// <summary>
/// Gaze input interface. GazeData returns Vector3(x, y, blink)
/// where x, y are normalized [0..1] and blink is 0 or 1.
/// </summary>
public interface IGazeInput
{
    UnityEngine.Vector3 GazeData { get; }
    bool IsAvailable { get; }
}
