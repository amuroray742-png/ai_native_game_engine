using System;
using System.Collections.Generic;
using AiNative.Core;

namespace AiNative.Hosting
{
    /// <summary>不保留完整观察的事件批次；消费一次后由宿主管理效果生命周期。</summary>
    public sealed class PresentationEvents
    {
        internal PresentationEvents(object generation, long tick, IReadOnlyList<InputResult> inputs, IReadOnlyList<GameEvent> events)
        {
            Generation = generation;
            Tick = tick;
            Inputs = inputs;
            Events = events;
        }
        internal object Generation { get; }
        public long Tick { get; }
        public IReadOnlyList<InputResult> Inputs { get; }
        public IReadOnlyList<GameEvent> Events { get; }
    }

    /// <summary>实时策略仅保留相邻观察；事件独立排队，不随观察覆盖丢失。</summary>
    public sealed class RealtimePresentationBuffer<T>
    {
        private readonly Queue<PresentationEvents> _events = new Queue<PresentationEvents>();
        private object _generation = new object();
        public RealtimePresentationBuffer(Observation<T> initial)
        {
            Current = initial ?? throw new ArgumentNullException(nameof(initial));
            Previous = initial;
        }
        public Observation<T> Previous { get; private set; }
        public Observation<T> Current { get; private set; }
        public int PendingEventBatches => _events.Count;

        public void Push(TickResult<T> frame)
        {
            if (frame == null || !Current.IsFromSameGame(frame.Observation) || Current.Tick == long.MaxValue || frame.Tick != Current.Tick + 1)
            {
                throw new InvalidOperationException("实时帧必须来自同一实例且 Tick 连续。");
            }
            Previous = Current;
            Current = frame.Observation;
            if (frame.Events.Count != 0 || frame.Inputs.Count != 0)
            {
                _events.Enqueue(new PresentationEvents(_generation, frame.Tick, frame.Inputs, frame.Events));
            }
        }
        public PresentationEvents? TakeEvents() => _events.Count == 0 ? null : _events.Dequeue();
        public bool IsActive(PresentationEvents events) => events != null && ReferenceEquals(events.Generation, _generation);

        public void Reset(Observation<T> initial)
        {
            if (initial == null)
            {
                throw new ArgumentNullException(nameof(initial));
            }
            _generation = new object();
            _events.Clear();
            Previous = initial;
            Current = initial;
        }

        // 相机/UI 刷新仍由宿主负责；不能把较晚的画面标注成较早 Tick。
        public bool IsAt(Observation<T> target) => target != null && Current.IsFromSameGame(target)
            && Current.Tick == target.Tick && target.Pending == 0 && (_events.Count == 0 || _events.Peek().Tick > target.Tick);
    }
}
