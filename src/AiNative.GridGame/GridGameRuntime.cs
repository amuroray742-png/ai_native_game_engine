using System;
using AiNative.Core;

namespace AiNative.GridGame
{
    /// <summary>单线程对局入口；独占规则和会话，不向宿主暴露可执行的规则引用。</summary>
    public sealed class GridGameRuntime
    {
        private readonly object _identity = new object();
        private readonly GridGameRules _rules;
        private readonly SimulationSession<GridCommand, GridGameView> _session;

        private GridGameRuntime(GridGameRules rules, SimulationSession<GridCommand, GridGameView> session)
        {
            _rules = rules;
            _session = session;
        }

        public bool IsFaulted => _session.IsFaulted;
        public int PendingCount => _session.PendingCount;
        public long LastSubmittedSequence => _session.LastSubmittedSequence;
        public long CompletedStepCount => _session.CompletedStepCount;
        public ulong RandomState => _session.RandomState;

        /// <summary>先校验并复制配置，再创建拥有独立随机源的新对局。</summary>
        public static GridGameRuntime Create(ScenarioDefinition configuration, ulong seed)
        {
            GridGameRules rules = GridGameRules.Create(configuration);
            return new GridGameRuntime(rules, new SimulationSession<GridCommand, GridGameView>(rules, seed));
        }

        /// <summary>校验完整状态并创建候选对局；失败不影响调用方原有对局。</summary>
        public static GridGameRuntime Restore(GridGameSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            GridGameRules rules = GridGameRules.Restore(snapshot.Game);
            if (snapshot.Game.TurnIndex - 1 > snapshot.Session.CompletedStepCount)
            {
                throw new ArgumentException("玩法回合不能超过会话已完成步数。", nameof(snapshot));
            }
            return new GridGameRuntime(rules,
                SimulationSession<GridCommand, GridGameView>.Restore(rules, snapshot.Session));
        }

        /// <summary>仅接收入队，返回序号；合法性在 Advance 中判断。</summary>
        public long Submit(GridCommand command) => _session.Submit(command);

        /// <summary>执行一条命令并立即捕获本步状态；空队列返回 null，异常后禁止继续使用。</summary>
        public GridStep? Advance()
        {
            StepResult? result = _session.Advance();
            return result == null ? null : new GridStep(result, Capture());
        }

        /// <summary>返回隔离视图，不执行命令；故障对局拒绝查询。</summary>
        public GridGameView GetState() => _session.GetState();

        /// <summary>同步捕获玩法、随机和调度位置；允许队列非空，不等同于可恢复快照。</summary>
        public GridObservation Capture()
        {
            // 通过会话验证故障/重入边界；规则引用始终属于当前对局。
            _session.GetState();
            return new GridObservation(_identity, _rules.ExportState(), _session.RandomState,
                _session.LastSubmittedSequence, _session.CompletedStepCount, _session.PendingCount);
        }

        /// <summary>仅健康空队列边界可保存；失败不推进或丢弃队列。</summary>
        public GridGameSnapshot ExportState()
        {
            SessionState session = _session.ExportState();
            return new GridGameSnapshot(session, _rules.ExportState());
        }
    }
}
