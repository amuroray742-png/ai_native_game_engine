using System;
using System.Collections.Generic;
using AiNative.Core;

namespace AiNative.Hosting
{
    /// <summary>逐步动画策略。写画面前检查 IsActive；检查、写入与确认须在同一主线程同步调用。</summary>
    public sealed class SequentialPresentationQueue<T>
    {
        private readonly Queue<TickResult<T>> _pending = new Queue<TickResult<T>>();
        private TickResult<T>? _active;
        private long _lastTick;
        public SequentialPresentationQueue(Observation<T> initial)
        {
            Presented = initial ?? throw new ArgumentNullException(nameof(initial));
            _lastTick = initial.Tick;
        }
        public Observation<T> Presented { get; private set; }
        public int PendingCount => _pending.Count + (_active == null ? 0 : 1);

        public void Enqueue(TickResult<T> frame)
        {
            if (frame == null || !Presented.IsFromSameGame(frame.Observation) || _lastTick == long.MaxValue || frame.Tick != _lastTick + 1)
            {
                throw new InvalidOperationException("表现帧必须来自同一实例且 Tick 连续。");
            }
            _pending.Enqueue(frame);
            _lastTick = frame.Tick;
        }
        public TickResult<T>? BeginNext()
        {
            if (_active != null)
            {
                throw new InvalidOperationException("当前动画尚未完成。");
            }
            _active = _pending.Count == 0 ? null : _pending.Dequeue();
            return _active;
        }
        public bool IsActive(TickResult<T> frame) => frame != null && ReferenceEquals(frame, _active);
        public bool Complete(TickResult<T> frame)
        {
            if (!IsActive(frame))
            {
                return false;
            }
            Presented = frame.Observation;
            _active = null;
            return true;
        }
        public void Reset(Observation<T> initial)
        {
            if (initial == null)
            {
                throw new ArgumentNullException(nameof(initial));
            }
            _pending.Clear();
            _active = null;
            Presented = initial;
            _lastTick = initial.Tick;
        }
        public bool IsSettledAt(Observation<T> target) => target != null && Presented.IsFromSameGame(target)
            && Presented.Tick == target.Tick && target.Pending == 0 && PendingCount == 0;
    }
}
