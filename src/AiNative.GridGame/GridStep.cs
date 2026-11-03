using AiNative.Core;

namespace AiNative.GridGame
{
    /// <summary>同一次同步观察中的玩法、随机及调度数据；历史观察不会随推进改变。</summary>
    public sealed class GridObservation
    {
        internal GridObservation(object owner, GridGameState game, ulong randomState,
            long lastSubmittedSequence, long completedStepCount, int pendingCount)
        {
            Owner = owner;
            Game = game;
            RandomState = randomState;
            LastSubmittedSequence = lastSubmittedSequence;
            CompletedStepCount = completedStepCount;
            PendingCount = pendingCount;
        }

        // 仅用于表现队列识别重载后的实例；不进入任何摘要或持久化格式。
        internal object Owner { get; }
        public GridGameState Game { get; }
        public ulong RandomState { get; }
        public long LastSubmittedSequence { get; }
        public long CompletedStepCount { get; }
        public int PendingCount { get; }

        /// <summary>判断是否来自同一个对局实例；恢复得到的新实例具有不同身份。</summary>
        public bool IsFromSameGame(GridObservation other) => other != null && ReferenceEquals(Owner, other.Owner);
    }

    /// <summary>已完成命令的结果及其精确后状态；表现层不得用较晚查询替换 State。</summary>
    public sealed class GridStep
    {
        internal GridStep(StepResult result, GridObservation state)
        {
            Step = result;
            State = state;
        }

        public StepResult Step { get; }
        public GridObservation State { get; }
    }
}
