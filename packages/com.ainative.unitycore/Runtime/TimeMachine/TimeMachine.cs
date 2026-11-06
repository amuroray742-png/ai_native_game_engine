using System;
using System.Collections.Generic;
using System.Diagnostics;
using AiNative.UnityCore.Pool;
using UnityEngine.Pool;
using UnityEngine.Profiling;

namespace AiNative.UnityCore.TimeMachine
{
    /// <summary>
    /// 负责集中管理所有的时间驱动逻辑
    /// </summary>
    public class TimeMachine : ITimeMachine
    {
        private readonly struct UpdateCallbackData
        {
            public readonly Action<float> Action;
            public readonly string ProfilerSampleName;

            public UpdateCallbackData(Action<float> action, string profilerSampleName)
            {
                Action = action;
                ProfilerSampleName = profilerSampleName;
            }
        }

        private readonly struct LateUpdateCallbackData
        {
            public readonly Action<float> Action;
            public readonly string ProfilerSampleName;

            public LateUpdateCallbackData(Action<float> action, string profilerSampleName)
            {
                Action = action;
                ProfilerSampleName = profilerSampleName;
            }
        }

        private readonly struct FixedUpdateCallbackData
        {
            public readonly Action<float> Action;
            public readonly string ProfilerSampleName;

            public FixedUpdateCallbackData(Action<float> action, string profilerSampleName)
            {
                Action = action;
                ProfilerSampleName = profilerSampleName;
            }
        }

        private readonly struct OmegaLateUpdateCallbackData
        {
            public readonly Action<long> Action;
            public readonly string ProfilerSampleName;

            public OmegaLateUpdateCallbackData(Action<long> action, string profilerSampleName)
            {
                Action = action;
                ProfilerSampleName = profilerSampleName;
            }
        }

        public struct ActionOrder<T>
        {
            public Action<T> Action;
            public int Order;

            public ActionOrder(Action<T> action, int order)
            {
                Action = action;
                Order = order;
            }
        }

        public class Timer
        {
            public Action<int> Action;
            public string ProfilerSampleName;
            public int Order;
            public float IntervalS;
            public int Repeats;

            public float TimerS;
            public int RepeatCount;

            public bool IsMarkedForDelete { get; private set; }

            public Timer() { }

            public void Init(Action<int> action, string profilerSampleName, int order, float intervalS, int repeats)
            {
                Action = action;
                ProfilerSampleName = profilerSampleName;
                Order = order;
                IntervalS = intervalS;
                Repeats = repeats;
                TimerS = 0;
                RepeatCount = 0;
                IsMarkedForDelete = false;
            }

            public void Reset(Action<int> action, string profilerSampleName, int order, float intervalS, int repeats)
            {
                Action = action;
                ProfilerSampleName = profilerSampleName;
                Order = order;
                IntervalS = intervalS;
                Repeats = repeats;
                TimerS = 0;
                RepeatCount = 0;
                IsMarkedForDelete = false;
            }

            public void MarkForDelete()
            {
                IsMarkedForDelete = true;
            }

            public void OnUpdate(float dt)
            {
                if (IsMarkedForDelete) return;

                if (RepeatCount < Repeats || Repeats < 0)
                {
                    TimerS += dt;
                    if (TimerS >= IntervalS)
                    {
                        TimerS = 0;
                        RepeatCount++;
                        Profiler.BeginSample(ProfilerSampleName);
                        try
                        {
                            Action.Invoke(RepeatCount);
                        }
                        finally
                        {
                            Profiler.EndSample();
                        }
                    }

                    if (Repeats > 0 && RepeatCount >= Repeats)
                    {
                        IsMarkedForDelete = true;
                    }
                }
            }
        }

        // Update
        private readonly List<int> _listUpdateOrder = new List<int>();
        private readonly Dictionary<int, Dictionary<int, UpdateCallbackData>> _dictUpdateOrderHashIDAction = new Dictionary<int, Dictionary<int, UpdateCallbackData>>();
        private readonly Dictionary<int, ActionOrder<float>> _dictUpdateHashIDAction = new Dictionary<int, ActionOrder<float>>();
        private bool _isUpdateRunning = false;

        // LateUpdate
        private readonly List<int> _listLateUpdateOrder = new List<int>();
        private readonly Dictionary<int, Dictionary<int, LateUpdateCallbackData>> _dictLateUpdateOrderHashIDAction = new Dictionary<int, Dictionary<int, LateUpdateCallbackData>>();
        private readonly Dictionary<int, ActionOrder<float>> _dictLateUpdateHashIDAction = new Dictionary<int, ActionOrder<float>>();
        private bool _isLateUpdateRunning = false;

        // FixedUpdate
        private readonly List<int> _listFixedUpdateOrder = new List<int>();
        private readonly Dictionary<int, Dictionary<int, FixedUpdateCallbackData>> _dictFixedUpdateOrderHashIDAction = new Dictionary<int, Dictionary<int, FixedUpdateCallbackData>>();
        private readonly Dictionary<int, ActionOrder<float>> _dictFixedUpdateHashIDAction = new Dictionary<int, ActionOrder<float>>();
        private bool _isFixedUpdateRunning = false;

        // 给资源加载和实例化Prefab用
        private readonly List<int> _listOmegaLateUpdateOrder = new List<int>();
        private readonly Dictionary<int, Dictionary<int, OmegaLateUpdateCallbackData>> _dictOmegaLateUpdateOrderHashIDAction = new Dictionary<int, Dictionary<int, OmegaLateUpdateCallbackData>>();
        private readonly Dictionary<int, ActionOrder<long>> _dictOmegaLateUpdateHashIDAction = new Dictionary<int, ActionOrder<long>>();
        private bool _isOmegaLateUpdateRunning = false;

        private readonly List<int> _listTimerUpdateOrder = new List<int>();
        private readonly Dictionary<int, Dictionary<int, Timer>> _dictTimerUpdateOrderHashIDAction = new Dictionary<int, Dictionary<int, Timer>>();
        private readonly Dictionary<int, Timer> _dictTimerUpdateHashIDTimer = new Dictionary<int, Timer>();
        private bool _isTimerUpdateRunning = false;

        private ObjectPool<Timer> _timersPool = new ObjectPool<Timer>(() => { return new Timer(); }, null, (obj) =>
        {
            obj.Action = null;
            obj.ProfilerSampleName = null;
            obj.TimerS = 0;
            obj.RepeatCount = 0;
        }, null, false, 20);

        private List<Timer> _timersToAdd = new List<Timer>();
        private Dictionary<int, Timer> _dictTimersToAdd = new Dictionary<int, Timer>();

        Stopwatch _stopwatch;
        private bool _curFrameStart = false;
        private long _curFrameStartTime = 0;

        public long CurrentFrameElapsedTimeTicks
        { 
            get
            {
                return _stopwatch.ElapsedTicks - _curFrameStartTime;
            }
        }

        public ITimeTicket RegisterUpdate(Action<float> updateAction, string profilerSampleName, int order = 0)
        {
            if (updateAction == null) return default;
            int hashCode = updateAction.GetHashCode();
            if (_dictUpdateHashIDAction.TryGetValue(hashCode, out var action))
            {
                return new TimeTicket(hashCode, TicketType.Update);
            }
            else
            {
                _dictUpdateHashIDAction.Add(hashCode, new ActionOrder<float>(updateAction, order));

                //dict order
                if (!_dictUpdateOrderHashIDAction.ContainsKey(order))
                    _dictUpdateOrderHashIDAction.Add(order, new Dictionary<int, UpdateCallbackData>());
                var callbackData = new UpdateCallbackData(updateAction, profilerSampleName);
                if (!_dictUpdateOrderHashIDAction[order].ContainsKey(hashCode))
                    _dictUpdateOrderHashIDAction[order].Add(hashCode, callbackData);
                else
                    _dictUpdateOrderHashIDAction[order][hashCode] = callbackData;

                //order list 
                if (!_listUpdateOrder.Contains(order))
                {
                    _listUpdateOrder.Add(order);
                    //排序
                    _listUpdateOrder.Sort();
                }

                if (_dictUpdateHashIDAction.Count > 0 && _isUpdateRunning == false)
                {
                    _isUpdateRunning = true;
                }

                return new TimeTicket(hashCode, TicketType.Update);
            }
        }
        
        public void RemoveUpdate(Action<float> updateAction)
        {
            RemoveUpdate(updateAction.GetHashCode());
        }

        public void RemoveUpdate(int actionId)
        {
            if (_dictUpdateHashIDAction.TryGetValue(actionId, out var ao))
            {
                if(_dictUpdateOrderHashIDAction.TryGetValue(ao.Order,out var idAction))
                {
                    idAction.Remove(actionId);
                    if(idAction.Count == 0)
                    {
                        _listUpdateOrder.Remove(ao.Order);
                    }
                }
                _dictUpdateHashIDAction.Remove(actionId);
            }

            if(_dictUpdateHashIDAction.Count == 0)
            {
                if(_isUpdateRunning)
                {
                    _isUpdateRunning = false;
                }
            }
        }


        public ITimeTicket RegisterLateUpdate(Action<float> lateUpdateAction, string profilerSampleName, int order = 0)
        {
            if (lateUpdateAction == null) return default;
            int hashCode = lateUpdateAction.GetHashCode();
            if (_dictLateUpdateHashIDAction.TryGetValue(hashCode, out var action))
            {
                return new TimeTicket(hashCode, TicketType.LateUpdate);
            }
            else
            {
                _dictLateUpdateHashIDAction.Add(hashCode, new ActionOrder<float>(lateUpdateAction, order));

                //dict order
                if (!_dictLateUpdateOrderHashIDAction.ContainsKey(order))
                    _dictLateUpdateOrderHashIDAction.Add(order, new Dictionary<int, LateUpdateCallbackData>());
                var callbackData = new LateUpdateCallbackData(lateUpdateAction, profilerSampleName);
                if (!_dictLateUpdateOrderHashIDAction[order].ContainsKey(hashCode))
                    _dictLateUpdateOrderHashIDAction[order].Add(hashCode, callbackData);
                else
                    _dictLateUpdateOrderHashIDAction[order][hashCode] = callbackData;

                //order list 
                if (!_listLateUpdateOrder.Contains(order))
                {
                    _listLateUpdateOrder.Add(order);
                    //排序
                    _listLateUpdateOrder.Sort();
                }

                if (_dictLateUpdateHashIDAction.Count > 0 && _isLateUpdateRunning == false)
                {
                    _isLateUpdateRunning = true;
                }

                return new TimeTicket(hashCode, TicketType.LateUpdate);
            }
        }

        public void RemoveLateUpdate(Action<float> lateUpdateAction)
        {
            RemoveLateUpdate(lateUpdateAction.GetHashCode());
        }

        public void RemoveLateUpdate(int actionId)
        {
            if (_dictLateUpdateHashIDAction.TryGetValue(actionId, out var ao))
            {
                if(_dictLateUpdateOrderHashIDAction.TryGetValue(ao.Order,out var idAction))
                {
                    idAction.Remove(actionId);
                    if(idAction.Count == 0)
                    {
                        _listLateUpdateOrder.Remove(ao.Order);
                    }
                }
                _dictLateUpdateHashIDAction.Remove(actionId);
            }

            if(_dictLateUpdateHashIDAction.Count == 0)
            {
                if(_isLateUpdateRunning)
                {
                    _isLateUpdateRunning = false;
                }
            }
        }

        public ITimeTicket RegisterFixedUpdate(Action<float> fixedUpdateAction, string profilerSampleName, int order = 0)
        {
            if (fixedUpdateAction == null) return default;
            int hashCode = fixedUpdateAction.GetHashCode();
            if (_dictFixedUpdateHashIDAction.TryGetValue(hashCode, out var action))
            {
                return new TimeTicket(hashCode, TicketType.FixedUpdate);
            }
            else
            {
                _dictFixedUpdateHashIDAction.Add(hashCode, new ActionOrder<float>(fixedUpdateAction, order));

                //dict order
                if (!_dictFixedUpdateOrderHashIDAction.ContainsKey(order))
                    _dictFixedUpdateOrderHashIDAction.Add(order, new Dictionary<int, FixedUpdateCallbackData>());
                var callbackData = new FixedUpdateCallbackData(fixedUpdateAction, profilerSampleName);
                if (!_dictFixedUpdateOrderHashIDAction[order].ContainsKey(hashCode))
                    _dictFixedUpdateOrderHashIDAction[order].Add(hashCode, callbackData);
                else
                    _dictFixedUpdateOrderHashIDAction[order][hashCode] = callbackData;

                //order list 
                if (!_listFixedUpdateOrder.Contains(order))
                {
                    _listFixedUpdateOrder.Add(order);
                    //排序
                    _listFixedUpdateOrder.Sort();
                }

                if (_dictFixedUpdateHashIDAction.Count > 0 && _isFixedUpdateRunning == false)
                {
                    _isFixedUpdateRunning = true;
                }

                return new TimeTicket(hashCode, TicketType.FixedUpdate);
            }
        }

        public void RemoveFixedUpdate(Action<float> fixedUpdateAction)
        {
            RemoveFixedUpdate(fixedUpdateAction.GetHashCode());
        }

        public void RemoveFixedUpdate(int actionId)
        {
            if (_dictLateUpdateHashIDAction.TryGetValue(actionId, out var ao))
            {
                if (_dictLateUpdateOrderHashIDAction.TryGetValue(ao.Order, out var idAction))
                {
                    idAction.Remove(actionId);
                    if (idAction.Count == 0)
                    {
                        _listLateUpdateOrder.Remove(ao.Order);
                    }
                }
                _dictLateUpdateHashIDAction.Remove(actionId);
            }

            if (_dictLateUpdateHashIDAction.Count == 0)
            {
                if (_isLateUpdateRunning)
                {
                    _isLateUpdateRunning = false;
                }
            }
        }

        public void OnUpdate(float dt)
        {
            if (!_isUpdateRunning) return;

            for (var i = _listUpdateOrder.Count - 1; i >= 0; i--) 
            {
                var item = _dictUpdateOrderHashIDAction[_listUpdateOrder[i]].GetEnumerator();
                while (item.MoveNext())
                {
                    var callbackData = item.Current.Value;
                    Profiler.BeginSample(callbackData.ProfilerSampleName);
                    try
                    {
                        callbackData.Action.Invoke(dt);
                    }
                    finally
                    {
                        Profiler.EndSample();
                    }
                }
            }
        }

        public void OnLateUpdate(float dt)
        {
            if (!_isLateUpdateRunning) return;

            for (var i = _listLateUpdateOrder.Count - 1; i >= 0; i--)
            {
                var item = _dictLateUpdateOrderHashIDAction[_listLateUpdateOrder[i]].GetEnumerator();
                while (item.MoveNext())
                {
                    var callbackData = item.Current.Value;
                    Profiler.BeginSample(callbackData.ProfilerSampleName);
                    try
                    {
                        callbackData.Action.Invoke(dt);
                    }
                    finally
                    {
                        Profiler.EndSample();
                    }
                }
            }
        }

        public void OnFixedUpdate(float dt)
        {
            if (!_isFixedUpdateRunning) return;

            for (var i = _listFixedUpdateOrder.Count - 1; i >= 0; i--)
            {
                var item = _dictFixedUpdateOrderHashIDAction[_listFixedUpdateOrder[i]].GetEnumerator();
                while (item.MoveNext())
                {
                    var callbackData = item.Current.Value;
                    Profiler.BeginSample(callbackData.ProfilerSampleName);
                    try
                    {
                        callbackData.Action.Invoke(dt);
                    }
                    finally
                    {
                        Profiler.EndSample();
                    }
                }
            }
        }

        public void OnOmegaLateUpdate(long elapsedTicks)
        {
            if (!_isOmegaLateUpdateRunning) return;

            for (var i = _listOmegaLateUpdateOrder.Count - 1; i >= 0; i--)
            {
                var item = _dictOmegaLateUpdateOrderHashIDAction[_listOmegaLateUpdateOrder[i]].GetEnumerator();
                while (item.MoveNext())
                {
                    var callbackData = item.Current.Value;
                    Profiler.BeginSample(callbackData.ProfilerSampleName);
                    try
                    {
                        callbackData.Action.Invoke(elapsedTicks);
                    }
                    finally
                    {
                        Profiler.EndSample();
                    }
                }
            }
        }

        public void StartFrameLogicTimer()
        {
            _stopwatch = Stopwatch.StartNew();
            _curFrameStart = false;
        }

        public void SetFrameStartTime()
        {
            if (!_curFrameStart)
            {
                _curFrameStart = true;
                _curFrameStartTime = _stopwatch.ElapsedTicks;
            }
        }

        public void FrameTimingEnd()
        {
            _curFrameStart = false;
        }

        // public void StopFrameLogicTimer()
        // {
        //     if (!_stopwatch.IsRunning)
        //     {
        //         Core.Log.Error("没调用StartStopwatch，请成对调用");
        //         return;
        //     }

        //     _stopwatch.Stop();
        // }

        public ITimeTicket RegisterOmegaLateUpdate(Action<long> lateUpdateAction, string profilerSampleName, int order = 0)
        {
            if (lateUpdateAction == null) return default;
            int hashCode = lateUpdateAction.GetHashCode();
            if (_dictOmegaLateUpdateHashIDAction.TryGetValue(hashCode, out var action))
            {
                return new TimeTicket(hashCode, TicketType.OmegaLateUpdate);
            }
            else
            {
                _dictOmegaLateUpdateHashIDAction.Add(hashCode, new ActionOrder<long>(lateUpdateAction, order));

                //dict order
                if (!_dictOmegaLateUpdateOrderHashIDAction.ContainsKey(order))
                    _dictOmegaLateUpdateOrderHashIDAction.Add(order, new Dictionary<int, OmegaLateUpdateCallbackData>());
                var callbackData = new OmegaLateUpdateCallbackData(lateUpdateAction, profilerSampleName);
                if (!_dictOmegaLateUpdateOrderHashIDAction[order].ContainsKey(hashCode))
                    _dictOmegaLateUpdateOrderHashIDAction[order].Add(hashCode, callbackData);
                else
                    _dictOmegaLateUpdateOrderHashIDAction[order][hashCode] = callbackData;

                //order list 
                if (!_listOmegaLateUpdateOrder.Contains(order))
                {
                    _listOmegaLateUpdateOrder.Add(order);
                    //排序
                    _listOmegaLateUpdateOrder.Sort();
                }

                if (_dictOmegaLateUpdateHashIDAction.Count > 0 && _isOmegaLateUpdateRunning == false)
                {
                    _isOmegaLateUpdateRunning = true;
                }

                return new TimeTicket(hashCode, TicketType.OmegaLateUpdate);
            }
        }

        public void RemoveOmegaLateUpdate(Action<long> lateUpdateAction)
        {
            RemoveOmegaLateUpdate(lateUpdateAction.GetHashCode());
        }

        public void RemoveOmegaLateUpdate(int actionId)
        {
            if (_dictOmegaLateUpdateHashIDAction.TryGetValue(actionId, out var ao))
            {
                if(_dictOmegaLateUpdateOrderHashIDAction.TryGetValue(ao.Order,out var idAction))
                {
                    idAction.Remove(actionId);
                    if(idAction.Count == 0)
                    {
                        _listOmegaLateUpdateOrder.Remove(ao.Order);
                    }
                }
                _dictOmegaLateUpdateHashIDAction.Remove(actionId);
            }

            if(_dictOmegaLateUpdateHashIDAction.Count == 0)
            {
                if(_isOmegaLateUpdateRunning)
                {
                    _isOmegaLateUpdateRunning = false;
                }
            }
        }

        public ITimeTicket RegisterTimer(Action<int> timerAction, float intervalS, string profilerSampleName, int repeats = -1, int order = 0)
        {
            if (intervalS <= 0)
            {
                Core.Log.Error("定时器的间隔必须大于0");
                return default;
            }
            if (timerAction == null) return default;
            int hashCode = timerAction.GetHashCode();
            if (_dictTimerUpdateHashIDTimer.TryGetValue(hashCode, out var timer))
            {
                timer.Reset(timerAction, profilerSampleName, order, intervalS, repeats);
                return new TimeTicket(hashCode, TicketType.Timer);
            }
            else
            {
                if (!_dictTimersToAdd.TryGetValue(hashCode,  out timer))
                {
                    timer = _timersPool.Get();
                    timer.Init(timerAction, profilerSampleName, order, intervalS, repeats);
                    _dictTimersToAdd.Add(hashCode, timer);
                    _timersToAdd.Add(timer);
                }
                else
                {
                    timer.Reset(timerAction, profilerSampleName, order, intervalS, repeats);
                }

                return new TimeTicket(hashCode, TicketType.Timer);
            }
        }

        public void RemoveTimer(Action<int> timerAction)
        {
            RemoveTimer(timerAction.GetHashCode());
        }

        public void RemoveTimer(int actionId)
        {
            Timer timer;
            if (_dictTimerUpdateHashIDTimer.TryGetValue(actionId, out timer))
            {
                // 只是标记删除，等到一次Update之后集中处理
                timer.MarkForDelete();
            }
            else if (_dictTimersToAdd.TryGetValue(actionId, out timer))
            {
                timer.MarkForDelete();
            }
        }

        public void OnTimerUpdate(float dt)
        {
            if (_timersToAdd.Count > 0)
            {
                bool listOrderSort = false;
                for (int i = 0; i < _timersToAdd.Count; i++)
                {
                    var timer = _timersToAdd[i];
                    if (timer.IsMarkedForDelete)
                    {
                        _timersPool.Release(timer);
                        continue;
                    }
                    int hashCode = timer.Action.GetHashCode();
                    int order = timer.Order;

                    _dictTimerUpdateHashIDTimer.Add(hashCode, timer);

                    //dict order
                    if (!_dictTimerUpdateOrderHashIDAction.ContainsKey(order))
                        _dictTimerUpdateOrderHashIDAction.Add(order, new Dictionary<int, Timer>());
                    if (!_dictTimerUpdateOrderHashIDAction[order].ContainsKey(hashCode))
                        _dictTimerUpdateOrderHashIDAction[order].Add(hashCode, timer);
                    else
                    {
                        Core.Log.Error("不可能已经有定时器已经注册到这里了");
                    }

                    //order list 
                    if (!_listTimerUpdateOrder.Contains(order))
                    {
                        _listTimerUpdateOrder.Add(order);
                        listOrderSort = true;
                    }

                    if (_listTimerUpdateOrder.Count > 0 && _isTimerUpdateRunning == false)
                    {
                        _isTimerUpdateRunning = true;
                    }
                }

                if (listOrderSort)
                {
                    _listTimerUpdateOrder.Sort();
                }

                _timersToAdd.Clear();
                _dictTimersToAdd.Clear();
            }

            if (!_isTimerUpdateRunning) return;

            List<Timer> timersToDelete = FastListPool<Timer>.Get();
            for (int i = _listTimerUpdateOrder.Count - 1; i >= 0; i--)
            {
                var item = _dictTimerUpdateOrderHashIDAction[_listTimerUpdateOrder[i]].GetEnumerator();
                while (item.MoveNext())
                {
                    var timer = item.Current.Value;
                    timer.OnUpdate(dt);
                    if (timer.IsMarkedForDelete)
                    {
                        timersToDelete.Add(timer);
                    }
                }
            }

            for(int i = 0; i < timersToDelete.Count; i++)
            {
                var timer = timersToDelete[i];
                int actionId = timer.Action.GetHashCode();
                if (_dictTimerUpdateOrderHashIDAction.TryGetValue(timer.Order, out var idAction))
                {
                    idAction.Remove(actionId);
                    if (idAction.Count == 0)
                    {
                        _listTimerUpdateOrder.Remove(timer.Order);
                    }
                }
                _dictTimerUpdateHashIDTimer.Remove(actionId);
                _timersPool.Release(timer);
            }

            timersToDelete.Clear();
            FastListPool<Timer>.Release(timersToDelete);
            if (_dictTimerUpdateHashIDTimer.Count == 0 && _isTimerUpdateRunning)
            {
                _isTimerUpdateRunning = false;
            }
        }

        public void ClearAll()
        {
            // TODO: 这里需要释放所有的Timer和清理所有的Update、LateUpdate、FixedUpdate、OmegaLateUpdate的回调
        }
    }
}
