using System;

namespace AiNative.Core
{
    /// <summary>健康且队列为空时的会话状态；不包含玩法状态，不能用于恢复故障实例。</summary>
    public sealed class SessionState
    {
        /// <summary>计数必须非负且相等，算法必须受支持；随机状态允许整个 UInt64 范围。</summary>
        public SessionState(long lastSubmittedSequence, long completedStepCount,
            string randomAlgorithm, ulong randomState)
        {
            if (randomAlgorithm == null)
            {
                throw new ArgumentNullException(nameof(randomAlgorithm));
            }
            if (lastSubmittedSequence < 0 || completedStepCount < 0 || lastSubmittedSequence != completedStepCount)
            {
                throw new ArgumentException("空队列会话的提交序号和完成步数必须非负且相等。");
            }
            if (randomAlgorithm != DeterministicRandom.Algorithm)
            {
                throw new ArgumentException("不支持的随机算法。", nameof(randomAlgorithm));
            }
            LastSubmittedSequence = lastSubmittedSequence;
            CompletedStepCount = completedStepCount;
            RandomAlgorithm = randomAlgorithm;
            RandomState = randomState;
        }

        public long LastSubmittedSequence { get; }
        public long CompletedStepCount { get; }
        public string RandomAlgorithm { get; }
        public ulong RandomState { get; }
    }
}
