using System;
using System.Collections.Generic;

namespace AiNative.Core
{
    public sealed class InputResult
    {
        internal InputResult(long sequence, CommandResult result)
        {
            Sequence = sequence;
            Result = result;
        }

        public long Sequence { get; }
        public CommandResult Result { get; }
    }

    /// <summary>同一实例、同一 Tick 的隔离观察；实例令牌不进入任何持久化格式。</summary>
    public sealed class Observation<T>
    {
        internal Observation(object owner, long tick, long submitted, long processed, int pending, T state)
        {
            Owner = owner;
            Tick = tick;
            Submitted = submitted;
            Processed = processed;
            Pending = pending;
            State = state;
        }

        private object Owner { get; }
        public long Tick { get; }
        public long Submitted { get; }
        public long Processed { get; }
        public int Pending { get; }
        public T State { get; }
        public bool IsFromSameGame(Observation<T> other) => other != null && ReferenceEquals(Owner, other.Owner);
    }

    public sealed class TickResult<T>
    {
        internal TickResult(Observation<T> observation, InputResult[] inputs, GameEvent[] events)
        {
            Observation = observation;
            Inputs = Array.AsReadOnly(inputs);
            Events = Array.AsReadOnly(events);
        }

        public Observation<T> Observation { get; }
        public long Tick => Observation.Tick;
        public IReadOnlyList<InputResult> Inputs { get; }
        public IReadOnlyList<GameEvent> Events { get; }
    }
}
