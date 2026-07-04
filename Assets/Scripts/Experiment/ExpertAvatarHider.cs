using UnityEngine;
using Photon.Pun;

// Worker-side: hides the Expert's avatar while the Assembly task runs. The Expert has no tracked
// body on the PC side, so its avatar sits at a fixed pose in the room; during Assembly the
// subject moves around the physical workspace and the floating avatar distracts and can occlude
// the build area. Only renderers under the remote player rig (PhotonView + PostureHandler, not
// mine) are toggled — gaze visualizers and the shared mesh live outside that hierarchy.
public class ExpertAvatarHider : MonoBehaviour
{
    private ExperimentManager2 _manager;
    private bool _hidden;

    public void Initialize(ExperimentManager2 manager)
    {
        _manager = manager;
        _manager.OnStateChanged += HandleStateChanged;
        HandleStateChanged(_manager.CurrentState);
    }

    private void OnDestroy()
    {
        if (_manager != null) _manager.OnStateChanged -= HandleStateChanged;
    }

    private void HandleStateChanged(ExperimentState state)
    {
        bool hide = state == ExperimentState.TaskRunning
                 && _manager != null
                 && _manager.CurrentStepType == StepType.Assembly;

        // While hiding, re-apply on EVERY state event (periodic resync re-broadcasts included):
        // the Expert object is re-instantiated with renderers enabled if it reconnects mid-task.
        if (hide)
        {
            SetExpertRenderers(false);
            _hidden = true;
        }
        else if (_hidden)
        {
            SetExpertRenderers(true);
            _hidden = false;
        }
    }

    private void SetExpertRenderers(bool visible)
    {
        foreach (var pv in FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
        {
            if (pv.IsMine) continue;                                  // own rig (already self-hidden)
            if (pv.GetComponent<PostureHandler>() == null) continue;  // player rigs only
            bool changed = false;
            foreach (var r in pv.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled != visible) changed = true;
                r.enabled = visible;
            }
            if (changed)
                FileLogger.Log("Worker", $"[ExpertAvatarHider] Expert avatar {(visible ? "shown" : "hidden")} ({pv.name}).");
        }
    }
}
