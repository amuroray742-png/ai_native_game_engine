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
            _machine.FrameTimingEnd();
            long elapsedTicks = _machine.CurrentFrameElapsedTimeTicks;
            // Core.Log.Debug($"已经消耗时间Ticks={elapsedTicks}, 毫秒={elapsedTicks / (Stopwatch.Frequency / 1000L)}");
            // 在这个里面进行资源加载，实例化和其他一些需要进行帧率控制的操作
            _machine.OnOmegaLateUpdate(elapsedTicks);
        }
    }
}
