// Shared experiment enums/types used across the V2 experiment stack
// (ExperimentManager2, SetupCoordinator, ExpertUI2, WorkerHUD2, tasks, video, logging).
// These previously lived at the top of the now-removed legacy ExperimentManager.cs;
// they were extracted here when the V1 stack was deleted so the V2 code keeps compiling.

public enum StepType : byte
{
    Noise          = 0,
    Task           = 1,
    Questionnaire  = 2,
    Assembly       = 3,
    Alignment      = 4,  // position-alignment gate — video feed ON, no timer, Enter to advance
    ConditionStart = 5,  // auto-generated: switches gaze mode, then questionnaire gate
    Launch         = 6,  // launches Python script, auto-advances immediately
}

public enum ExperimentState : byte
{
    Idle          = 0,
    Ready         = 1,
    WhiteNoise    = 2,
    TaskRunning   = 3,
    Questionnaire = 4,
    Finished      = 5,
    TaskComplete  = 6,
    NoiseComplete = 7,
    Setup         = 8
}

public class ExperimentStep
{
    public StepType Type;
    public string   Instruction      = string.Empty; // Remote Expert
    public string   LocalInstruction = string.Empty; // Local Worker
    public int      ConditionIndex   = -1;           // set for ConditionStart / Launch steps
    public string   ScriptArgs       = string.Empty; // baked in at expand time for Launch steps
}
