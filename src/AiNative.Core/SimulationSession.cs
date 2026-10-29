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

        /// <summary>返回接收序号，不代表规则验证成功。故障后禁止接收。</summary>
        public long Submit(TCommand command)
        {
            EnsureHealthy();
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
            EnsureHealthy();
            if (_commands.Count == 0)
            {
                return null;
            }
            (long sequence, TCommand command) = _commands.Dequeue();
            try
            {
                ulong previousRandomState = _random.State;
                CommandResult result = _rules.Execute(command, _random)
                    ?? throw new InvalidOperationException("规则返回了空结果。");
                if (!result.Succeeded && _random.State != previousRandomState)
                {
                    throw new InvalidOperationException("被拒绝的命令消耗了随机状态。");
                }
                return new StepResult(sequence, result);
            }
            catch
            {
                IsFaulted = true;
                throw;
            }
        }

        /// <summary>故障实例不再提供可用状态；视图必须由规则实现隔离。</summary>
        public TView GetState()
        {
            EnsureHealthy();
            try
            {
                return _rules.GetState();
            }
            catch
            {
                IsFaulted = true;
                throw;
            }
        }

        private void EnsureHealthy()
        {
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
