using UnityEngine;

/// <summary>
/// Assembly task controller.
/// The spatial reference grid is a physical printed sheet on the table —
/// no virtual overlay is needed or rendered.
///
/// Activates when ExperimentState == TaskRunning and StepType == Assembly.
/// </summary>
public class AssemblyTask : MonoBehaviour
{
    private ExperimentManager2 experimentManager2;

    private void Start()
    {
        experimentManager2 = Object.FindAnyObjectByType<ExperimentManager2>();
        if (experimentManager2 == null)
        {
            Debug.LogError("[AssemblyTask] ExperimentManager2 not found in scene.");
            return;
        }
        experimentManager2.OnStateChanged += OnStateChanged;
    }

    private void OnDestroy()
    {
        if (experimentManager2 != null)
            experimentManager2.OnStateChanged -= OnStateChanged;
    }

    private void OnStateChanged(ExperimentState newState)
    {
        bool run = newState == ExperimentState.TaskRunning
                && experimentManager2.CurrentStepType == StepType.Assembly;
        if (run) StartTask(); else EndTask();
    }

    public void StartTask()
    {
        enabled = true;
        Debug.Log("[AssemblyTask] Started.");
    }

    public void EndTask()
    {
        enabled = false;
        Debug.Log("[AssemblyTask] Ended.");
    }
}
