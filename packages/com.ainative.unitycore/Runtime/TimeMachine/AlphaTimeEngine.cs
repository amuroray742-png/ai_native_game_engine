using UnityEngine;

namespace AiNative.UnityCore.TimeMachine
{
    public class AlphaTimeEngine : MonoBehaviour
    {
        private static readonly Unity.Profiling.ProfilerMarker OnUpdateMarker = new Unity.Profiling.ProfilerMarker("TM.OnUpdate");
        private static readonly Unity.Profiling.ProfilerMarker OnTimerUpdateMarker = new Unity.Profiling.ProfilerMarker("TM.OnTimerUpdate");
        private ITimeMachine _machine;
        public void SetTimeMachine(ITimeMachine machine)
        {
            _machine = machine;
        }

        void Start()
        {
            _machine.StartFrameLogicTimer();
        }

        // Update is called once per frame
        void Update()
        {
            if (_machine != null)
            {
                float dt = Time.deltaTime;
                using (OnUpdateMarker.Auto())
                {
                    _machine.OnUpdate(dt);
                }
                using (OnTimerUpdateMarker.Auto())
                {
                    _machine.OnTimerUpdate(dt);
                }
            }
        }

        private void LateUpdate()
        {
            _machine?.OnLateUpdate(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            _machine?.SetFrameStartTime();
            _machine?.OnFixedUpdate(Time.fixedDeltaTime);
        }
    }
}
