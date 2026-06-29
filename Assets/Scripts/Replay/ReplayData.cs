using System.Collections.Generic;
using Newtonsoft.Json;

// Shared data types used by ExperimentLogger (writer) and ReplayManager (reader).

public class ReplayMeta
{
    public int    participantNumber;
    public string trialId;
    public int    conditionIndex;
    public string gazeMode;
    public string noiseLevel;
    public string stepType;
    public int    stepIndex;
    public long   startMs;

    // SharedMesh transform at trial start (calibrated position in world space)
    public float[] meshPos;   // [x, y, z]
    public float[] meshRot;   // [x, y, z, w]
    public float[] meshScale; // [x, y, z]

    // Voice audio — absolute path to session WAV + offset where this trial's audio begins
    public string voiceWavPath;
    public float  voiceStartSeconds;
}

public class ReplayHeadPose
{
    public float[] p; // [x, y, z]
    public float[] r; // [x, y, z, w]
}

public class ReplayFrameData
{
    public float          t;          // elapsed seconds since trial start
    public float[]        gaze;       // [x, y, blink]
    public ReplayHeadPose workerHead; // Worker (Quest) head pose
    public ReplayHeadPose expertHead; // Expert (PC) head pose

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float[][] handL; // 24 × [x, y, z] world positions — absent if not tracked

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float[][] handR;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float[] workerCtrl; // [x, y, z] controller world position — absent if not tracked
}

public class ReplayData
{
    public ReplayMeta            meta;
    public List<ReplayFrameData> frames;
}
