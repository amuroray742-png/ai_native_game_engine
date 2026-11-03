using System;

namespace AiNative.Core
{
    /// <summary>Tick 和输入分别计数。快照边界无待处理输入，但 Tick 不要求等于输入数。</summary>
    public sealed class RuntimeState
    {
        public RuntimeState(long tick, long submitted, long processed, ulong randomState, SimulationTiming timing)
        {
            if (tick < 0 || submitted < 0 || processed < 0 || submitted != processed
                || (tick == 0 && processed != 0))
            {
                throw new ArgumentException("无效的运行时保存边界。");
            }
            Tick = tick;
            Submitted = submitted;
            Processed = processed;
            RandomState = randomState;
            Timing = timing ?? throw new ArgumentNullException(nameof(timing));
        }

        public long Tick { get; }
        public long Submitted { get; }
        public long Processed { get; }
        public ulong RandomState { get; }
        public SimulationTiming Timing { get; }
    }

    public sealed class SimulationSnapshot<TState>
    {
        public SimulationSnapshot(RuntimeState runtime, TState game)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            Game = game ?? throw new ArgumentNullException(nameof(game));
        }

        public RuntimeState Runtime { get; }
        public TState Game { get; }
    }
}
