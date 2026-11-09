using UnityEngine;

namespace AiNative.UnityCore.TimeMachine
{
    public class OmegaTimeEngine : MonoBehaviour
    {
        private ITimeMachine _machine;
        public void SetTimeMachine(ITimeMachine machine)
        {
            _machine = machine;
        }

        private void LateUpdate()
        {
            if (_machine == null) { return; }
            try { _machine.OnOmegaLateUpdate(_machine.CurrentFrameElapsedTimeTicks); }
            finally { _machine.FrameTimingEnd(); }
        }
    }
}
