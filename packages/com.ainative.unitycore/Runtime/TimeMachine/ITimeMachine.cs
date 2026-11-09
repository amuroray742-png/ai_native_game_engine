using System;

namespace AiNative.UnityCore.TimeMachine
{
    public interface ITimeMachine
    {
        /// <summary>
        /// 注册Update回调
        /// </summary>
        /// <param name="updateAction"></param>
        /// <param name="profilerSampleName">回调执行时的 Unity Profiler 标记名称。</param>
        /// <param name="order"></param>
        /// <returns></returns>
        ITimeTicket RegisterUpdate(Action<float> updateAction, string profilerSampleName, int order = 0);
        /// <summary>
        /// 移除Update回调
        /// </summary>
        /// <param name="updateAction"></param>
        void RemoveUpdate(Action<float> updateAction);
        void RemoveUpdate(int actionId);

        /// <summary>
        /// 注册LateUpdate回调
        /// </summary>
        /// <param name="lateUpdateAction"></param>
        /// <param name="profilerSampleName">回调执行时的 Unity Profiler 标记名称。</param>
        /// <param name="order"></param>
        /// <returns></returns>
        ITimeTicket RegisterLateUpdate(Action<float> lateUpdateAction, string profilerSampleName, int order = 0);
        void RemoveLateUpdate(Action<float> updateAction);
        void RemoveLateUpdate(int actionId);

        /// <summary>
        /// 注册FixedUpdate回调
        /// </summary>
        /// <param name="fixedUpdateAction"></param>
        /// <param name="profilerSampleName">回调执行时的 Unity Profiler 标记名称。</param>
        /// <param name="order"></param>
        /// <returns></returns>
        ITimeTicket RegisterFixedUpdate(Action<float> fixedUpdateAction, string profilerSampleName, int order = 0);
        void RemoveFixedUpdate(Action<float> updateAction);
        void RemoveFixedUpdate(int actionId);

        /// <summary>
        /// 请不要调用
        /// </summary>
        void OnUpdate(float dt);
        /// <summary>
        /// 请不要调用
        /// </summary>
        void OnLateUpdate(float dt);
        /// <summary>
        /// 请不要调用
        /// </summary>
        void OnFixedUpdate(float dt);
        /// <summary>
        /// 当前帧已经消耗的时间
        /// </summary>
        long CurrentFrameElapsedTimeTicks { get; }
        /// <summary>
        /// 开始消耗计时
        /// </summary>
        void StartFrameLogicTimer();
        void SetFrameStartTime();
        void FrameTimingEnd();
        /// <summary>
        /// 注册资源加载、Prefab 实例化等帧预算回调，并指定 Unity Profiler 标记名称。
        /// </summary>
        ITimeTicket RegisterOmegaLateUpdate(Action<long> lateUpdateAction, string profilerSampleName, int order = 0);
        void RemoveOmegaLateUpdate(Action<long> updateAction);
        void RemoveOmegaLateUpdate(int actionId);

        public void OnOmegaLateUpdate(long elapsedTicks);

        /// <summary>
        /// 注册定时器
        /// </summary>
        /// <param name="timerAction"></param>
        /// <param name="intervalS"></param>
        /// <param name="profilerSampleName">定时器实际触发时的 Unity Profiler 标记名称。</param>
        /// <param name="repeats"></param>
        /// <param name="order"></param>
        /// <returns></returns>
        ITimeTicket RegisterTimer(Action<int> timerAction, float intervalS, string profilerSampleName, int repeats = -1, int order = 0);
        void RemoveTimer(Action<int> timerAction);
        void RemoveTimer(int actionId);
        void OnTimerUpdate(float dt);
        void ClearAll();
    }
}
