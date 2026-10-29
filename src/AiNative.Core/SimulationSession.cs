using System;
using System.Collections.Generic;

namespace AiNative.Core
{
    /// <summary>单线程、不可重入的模拟宿主。提交只入队，推进一次处理一条命令。</summary>
    public sealed class SimulationSession<TCommand, TView> where TCommand : class
    {
        private readonly Queue<(long Sequence, TCommand Command)> _commands = new Queue<(long, TCommand)>();
        private readonly IGameRules<TCommand, TView> _rules;
        private readonly DeterministicRandom _random;
        private long _lastSequence;
        private bool _isExecuting;

        public SimulationSession(IGameRules<TCommand, TView> rules, ulong seed)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _random = new DeterministicRandom(seed);
        }

        public bool IsFaulted
        {
            get; private set;
        }
        public ulong RandomState => _random.State;
        public int PendingCount => _commands.Count;

        public long LastSubmittedSequence => _lastSequence;
        public long CompletedStepCount { get; private set; }

        /// <summary>创建独占给定规则的新会话；恢复当前随机状态，不执行规则或替换已有实例。</summary>
        public static SimulationSession<TCommand, TView> Restore(IGameRules<TCommand, TView> rules, SessionState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            // SplitMix64 的构造参数就是内部状态；这里不是重新使用初始种子。
            var session = new SimulationSession<TCommand, TView>(rules, state.RandomState);
            session._lastSequence = state.LastSubmittedSequence;
            session.CompletedStepCount = state.CompletedStepCount;
            return session;
        }

        /// <summary>仅导出健康、非执行中且队列为空的会话；失败不推进或清空队列。</summary>
        public SessionState ExportState()
        {
            EnsureAvailable();
            if (_commands.Count != 0)
            {
                throw new InvalidOperationException("待执行命令未清空，不能导出会话状态。");
            }
            return new SessionState(_lastSequence, CompletedStepCount, DeterministicRandom.Algorithm, _random.State);
        }

        /// <summary>返回接收序号，不代表规则验证成功。故障后禁止接收。</summary>
        public long Submit(TCommand command)
        {
            EnsureAvailable();
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }
            long sequence = checked(_lastSequence + 1);
            _commands.Enqueue((sequence, command));
            _lastSequence = sequence;
            return sequence;
        }

        /// <summary>空队列返回 null。意外故障标记实例不可用并保留原异常。</summary>
        public StepResult? Advance()
        {
            EnsureAvailable();
            if (_commands.Count == 0)
            {
                return null;
            }
            (long sequence, TCommand command) = _commands.Dequeue();
            _isExecuting = true;
            try
            {
                ulong previousRandomState = _random.State;
                CommandResult result = _rules.Execute(command, _random)
                    ?? throw new InvalidOperationException("规则返回了空结果。");
                if (!result.Succeeded && _random.State != previousRandomState)
                {
                    throw new InvalidOperationException("被拒绝的命令消耗了随机状态。");
                }
                var step = new StepResult(sequence, result);
                CompletedStepCount = checked(CompletedStepCount + 1);
                return step;
            }
            catch
            {
                IsFaulted = true;
                throw;
            }
            finally
            {
                _isExecuting = false;
            }
        }

        /// <summary>故障实例不再提供可用状态；视图必须由规则实现隔离。</summary>
        public TView GetState()
        {
            EnsureAvailable();
            _isExecuting = true;
            try
            {
                return _rules.GetState();
            }
            catch
            {
                IsFaulted = true;
                throw;
            }
            finally
            {
                _isExecuting = false;
            }
        }

        private void EnsureAvailable()
        {
            if (_isExecuting)
            {
                throw new InvalidOperationException("模拟实例不允许重入。");
            }
            if (IsFaulted)
            {
                throw new InvalidOperationException("模拟实例已故障，必须重新创建。");
            }
        }
    }

    public sealed class StepResult
    {
        public StepResult(long sequence, CommandResult result)
        {
            Sequence = sequence;
            Result = result;
        }

        public long Sequence { get; }
        public CommandResult Result { get; }
    }
}
