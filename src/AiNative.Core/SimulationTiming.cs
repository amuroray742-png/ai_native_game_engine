using System;

namespace AiNative.Core
{
    /// <summary>创建时固定的逻辑时间。零频率表示手动模式；不读取墙上时钟。</summary>
    public sealed class SimulationTiming
    {
        public SimulationTiming(int ticksPerSecond)
        {
            if (ticksPerSecond < 0 || ticksPerSecond > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            }
            TicksPerSecond = ticksPerSecond;
        }

        public int TicksPerSecond { get; }
        public float DeltaSeconds => TicksPerSecond == 0 ? 0 : 1.0f / TicksPerSecond;
    }

    public sealed class StepContext
    {
        internal StepContext(long tick, SimulationTiming timing, DeterministicRandom random)
        {
            Tick = tick;
            Timing = timing;
            Random = random;
        }

        public long Tick { get; }
        public SimulationTiming Timing { get; }
        public DeterministicRandom Random { get; }
    }
}
