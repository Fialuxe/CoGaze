// Gaze input interface. GazeData = Vector3(x, y, blink); x/y normalized [0..1], blink 0 or 1.
public interface IGazeInput
{
    UnityEngine.Vector3 GazeData { get; }
    bool IsAvailable { get; }
}
