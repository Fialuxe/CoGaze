using UnityEngine;

// Assembly task controller; activates when ExperimentState == TaskRunning and StepType == Assembly.
public class AssemblyTask : MonoBehaviour
{
    private ExperimentManager2 _experimentManager2;

    private void Start()
    {
        _experimentManager2 = Object.FindAnyObjectByType<ExperimentManager2>();
        if (_experimentManager2 == null)
        {
            Debug.LogError("[AssemblyTask] ExperimentManager2 not found in scene.");
            return;
        }
        _experimentManager2.OnStateChanged += OnStateChanged;
    }

    private void OnDestroy()
    {
        if (_experimentManager2 != null)
            _experimentManager2.OnStateChanged -= OnStateChanged;
    }

    private void OnStateChanged(ExperimentState newState)
    {
        bool run = newState == ExperimentState.TaskRunning
                && _experimentManager2.CurrentStepType == StepType.Assembly;
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
