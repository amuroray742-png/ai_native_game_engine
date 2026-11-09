#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace AiNative.UnityCore.TimeMachine
{
    /// <summary>
    /// 应用表现回调调度器，不推进模拟 Tick。只允许主线程调用。
    /// 同类型同委托重注册替换旧注册；order 降序，同 order 按注册顺序。
    /// </summary>
    public sealed class TimeMachine : ITimeMachine
    {
        private sealed class Timer
        {
            internal readonly int Id;
            internal readonly Action<int> Action;
            internal readonly float IntervalSeconds;
            internal readonly string Sample;
            internal readonly int Repeats;
            internal readonly int Order;
            internal float ElapsedSeconds;
            internal int Count;

            internal Timer(int id, Action<int> action, float intervalSeconds, string sample, int repeats, int order)
            {
                Id = id;
                Action = action;
                IntervalSeconds = intervalSeconds;
                Sample = sample;
                Repeats = repeats;
                Order = order;
            }
        }

        private readonly CallbackGroup<float> _update = new CallbackGroup<float>();
        private readonly CallbackGroup<float> _lateUpdate = new CallbackGroup<float>();
        private readonly CallbackGroup<float> _fixedUpdate = new CallbackGroup<float>();
        private readonly CallbackGroup<long> _omega = new CallbackGroup<long>();
        private readonly Dictionary<int, Timer> _timers = new Dictionary<int, Timer>();
        private readonly Dictionary<Action<int>, int> _timerIds = new Dictionary<Action<int>, int>();
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private int _nextId;
        private bool _dispatchingTimers;
        private bool _frameStarted;
        private long _frameStartTicks;

        public long CurrentFrameElapsedTimeTicks => _stopwatch.ElapsedTicks - _frameStartTicks;

        public ITimeTicket RegisterUpdate(Action<float> action, string profilerSampleName, int order = 0)
        {
            return Register(_update, TicketType.Update, action, profilerSampleName, order);
        }

        public ITimeTicket RegisterLateUpdate(Action<float> action, string profilerSampleName, int order = 0)
        {
            return Register(_lateUpdate, TicketType.LateUpdate, action, profilerSampleName, order);
        }

        public ITimeTicket RegisterFixedUpdate(Action<float> action, string profilerSampleName, int order = 0)
        {
            return Register(_fixedUpdate, TicketType.FixedUpdate, action, profilerSampleName, order);
        }

        public ITimeTicket RegisterOmegaLateUpdate(Action<long> action, string profilerSampleName, int order = 0)
        {
            return Register(_omega, TicketType.OmegaLateUpdate, action, profilerSampleName, order);
        }

        public void RemoveUpdate(Action<float> action) { _update.Remove(action); }
        public void RemoveUpdate(int actionId) { _update.Remove(actionId); }
        public void RemoveLateUpdate(Action<float> action) { _lateUpdate.Remove(action); }
        public void RemoveLateUpdate(int actionId) { _lateUpdate.Remove(actionId); }
        public void RemoveFixedUpdate(Action<float> action) { _fixedUpdate.Remove(action); }
        public void RemoveFixedUpdate(int actionId) { _fixedUpdate.Remove(actionId); }
        public void RemoveOmegaLateUpdate(Action<long> action) { _omega.Remove(action); }
        public void RemoveOmegaLateUpdate(int actionId) { _omega.Remove(actionId); }

        public void OnUpdate(float dt) { ValidateDelta(dt); _update.Dispatch(dt); }
        public void OnLateUpdate(float dt) { ValidateDelta(dt); _lateUpdate.Dispatch(dt); }
        public void OnFixedUpdate(float dt) { ValidateDelta(dt); _fixedUpdate.Dispatch(dt); }
        public void OnOmegaLateUpdate(long elapsedTicks) { _omega.Dispatch(elapsedTicks); }

        public void StartFrameLogicTimer()
        {
            _stopwatch.Restart();
            _frameStartTicks = 0;
            _frameStarted = false;
        }

        public void SetFrameStartTime()
        {
            if (_frameStarted) { return; }
            _frameStarted = true;
            _frameStartTicks = _stopwatch.ElapsedTicks;
        }

        public void FrameTimingEnd() { _frameStarted = false; }

        public ITimeTicket RegisterTimer(Action<int> action, float intervalS, string profilerSampleName, int repeats = -1, int order = 0)
        {
            ValidateCallback(action, profilerSampleName);
            if (float.IsNaN(intervalS) || float.IsInfinity(intervalS) || intervalS <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalS));
            }
            if (repeats < -1) { throw new ArgumentOutOfRangeException(nameof(repeats)); }
            int id = AllocateId();
            RemoveTimer(action);
            if (repeats != 0)
            {
                _timers.Add(id, new Timer(id, action, intervalS, profilerSampleName, repeats, order));
                _timerIds.Add(action, id);
            }
            return new TimeTicket(id, TicketType.Timer, RemoveTimer, HasTimer);
        }

        public void RemoveTimer(Action<int> action)
        {
            if (action == null) { throw new ArgumentNullException(nameof(action)); }
            if (_timerIds.TryGetValue(action, out int id)) { RemoveTimer(id); }
        }

        public void RemoveTimer(int actionId)
        {
            if (_timers.TryGetValue(actionId, out Timer timer))
            {
                _timers.Remove(actionId);
                _timerIds.Remove(timer.Action);
            }
        }

        /// <summary>新增定时器下一次调用才参与派发；每个定时器最多触发一次，触发后重新计时。</summary>
        public void OnTimerUpdate(float dt)
        {
            ValidateDelta(dt);
            if (_dispatchingTimers) { throw new InvalidOperationException("Cannot reenter timer dispatch."); }
            if (_timers.Count == 0) { return; }
            var snapshot = new List<Timer>(_timers.Values);
            snapshot.Sort((left, right) =>
            {
                int comparison = right.Order.CompareTo(left.Order);
                return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
            });
            _dispatchingTimers = true;
            try
            {
                foreach (Timer timer in snapshot)
                {
                    if (!HasTimer(timer.Id)) { continue; }
                    timer.ElapsedSeconds += dt;
                    if (timer.ElapsedSeconds < timer.IntervalSeconds) { continue; }
                    timer.ElapsedSeconds = 0;
                    timer.Count = checked(timer.Count + 1);
                    Profiler.BeginSample(timer.Sample);
                    try { timer.Action(timer.Count); }
                    finally
                    {
                        Profiler.EndSample();
                        if (timer.Repeats > 0 && timer.Count >= timer.Repeats) { RemoveTimer(timer.Id); }
                    }
                }
            }
            finally { _dispatchingTimers = false; }
        }

        /// <summary>立即使全部票据和当前派发批次失效。ID 不回绕、不重置。</summary>
        public void ClearAll()
        {
            _update.Clear();
            _lateUpdate.Clear();
            _fixedUpdate.Clear();
            _omega.Clear();
            _timers.Clear();
            _timerIds.Clear();
            _stopwatch.Reset();
            _frameStartTicks = 0;
            _frameStarted = false;
        }

        private bool HasTimer(int id) { return _timers.ContainsKey(id); }

        private ITimeTicket Register<T>(CallbackGroup<T> group, TicketType type, Action<T> action, string sample, int order)
        {
            ValidateCallback(action, sample);
            int id = AllocateId();
            group.Register(id, action, sample, order);
            return new TimeTicket(id, type, group.Remove, group.Contains);
        }

        private int AllocateId()
        {
            int id = checked(_nextId + 1);
            _nextId = id;
            return id;
        }

        private static void ValidateCallback(Delegate action, string sample)
        {
            if (action == null) { throw new ArgumentNullException(nameof(action)); }
            if (sample == null) { throw new ArgumentNullException(nameof(sample)); }
        }

        private static void ValidateDelta(float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dt));
            }
        }
    }
}
