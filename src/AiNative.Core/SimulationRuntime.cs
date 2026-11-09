using System;
using System.Collections.Generic;

namespace AiNative.Core
{
    /// <summary>单线程、不可重入；每次 Advance 消费全部排队输入并推进一个 Tick。</summary>
    public sealed class SimulationRuntime<TCommand, TObservation, TState> : IDisposable where TCommand : class
    {
        private readonly object _identity = new object();
        private readonly Queue<(long Sequence, TCommand Command)> _pending = new Queue<(long, TCommand)>();
        private readonly IGameSimulation<TCommand, TObservation, TState> _game;
        private readonly DeterministicRandom _random;
        private bool _executing;
        private bool _disposed;

        private SimulationRuntime(IGameSimulation<TCommand, TObservation, TState> game, RuntimeState state)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _random = new DeterministicRandom(state.RandomState);
            Tick = state.Tick;
            Submitted = state.Submitted;
            Processed = state.Processed;
            Timing = state.Timing;
        }

        public long Tick { get; private set; }
        public long Submitted { get; private set; }
        public long Processed { get; private set; }
        public int PendingCount => _pending.Count;
        public ulong RandomState => _random.State;
        public SimulationTiming Timing { get; }
        public bool IsFaulted { get; private set; }

        /// <summary>宿主在结果编码或交付失败后终止此实例；已提交的 Tick 不回滚，之后不能推进或捕获状态。</summary>
        public void MarkFaulted()
        {
            IsFaulted = true;
        }

        public static SimulationRuntime<TCommand, TObservation, TState> Create(
            Func<IGameSimulation<TCommand, TObservation, TState>> factory, ulong seed, SimulationTiming timing)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            var state = new RuntimeState(0, 0, 0, seed, timing);
            return new SimulationRuntime<TCommand, TObservation, TState>(factory(), state);
        }

        public static SimulationRuntime<TCommand, TObservation, TState> Restore(
            SimulationSnapshot<TState> snapshot, Func<TState, IGameSimulation<TCommand, TObservation, TState>> factory)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            return new SimulationRuntime<TCommand, TObservation, TState>(factory(snapshot.Game), snapshot.Runtime);
        }

        public long Submit(TCommand command)
        {
            EnsureAvailable();
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }
            long sequence = checked(Submitted + 1);
            _pending.Enqueue((sequence, command));
            Submitted = sequence;
            return sequence;
        }

        public TickResult<TObservation> Advance()
        {
            EnsureAvailable();
            _executing = true;
            try
            {
                long nextTick = checked(Tick + 1);
                int count = _pending.Count;
                var inputs = new InputResult[count];
                var events = new List<GameEvent>();
                for (int index = 0; index < count; index++)
                {
                    (long sequence, TCommand command) = _pending.Dequeue();
                    ulong previousRandom = _random.State;
                    CommandResult result = _game.Execute(command, _random)
                        ?? throw new InvalidOperationException("规则返回空结果。");
                    if (!result.Succeeded && previousRandom != _random.State)
                    {
                        throw new InvalidOperationException("被拒绝的输入消耗了随机状态。");
                    }
                    inputs[index] = new InputResult(sequence, result);
                    events.AddRange(result.Events);
                    Processed = checked(Processed + 1);
                }
                IReadOnlyList<GameEvent> tickEvents = _game.Advance(new StepContext(nextTick, Timing, _random))
                    ?? throw new InvalidOperationException("规则返回空事件列表。");
                events.AddRange(tickEvents);
                Tick = nextTick;
                return new TickResult<TObservation>(Observe(), inputs, events.ToArray());
            }
            catch
            {
                IsFaulted = true;
                throw;
            }
            finally { _executing = false; }
        }

        public Observation<TObservation> Capture() => Read(Observe);
        public TObservation GetState() => Capture().State;

        public SimulationSnapshot<TState> ExportState()
        {
            EnsureAvailable();
            if (PendingCount != 0)
            {
                throw new InvalidOperationException("待执行输入未清空，不能保存。");
            }
            return Read(() => new SimulationSnapshot<TState>(
                new RuntimeState(Tick, Submitted, Processed, RandomState, Timing), _game.ExportState()));
        }

        // 验证检查点可捕获游戏数据；输入队列仍由记录的 submit/advance 操作完整描述。
        public TState CaptureGameState() => Read(_game.ExportState);

        private Observation<TObservation> Observe() => new Observation<TObservation>(
            _identity, Tick, Submitted, Processed, PendingCount, _game.GetState());

        private T Read<T>(Func<T> read)
        {
            EnsureAvailable();
            _executing = true;
            try
            {
                return read();
            }
            catch { IsFaulted = true; throw; }
            finally { _executing = false; }
        }

        private void EnsureAvailable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SimulationRuntime<TCommand, TObservation, TState>));
            }
            if (_executing)
            {
                throw new InvalidOperationException("模拟不允许重入。");
            }
            if (IsFaulted)
            {
                throw new InvalidOperationException("模拟已故障，必须重新创建。");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            if (_executing)
            {
                throw new InvalidOperationException("执行期间不能释放模拟。");
            }
            _disposed = true;
            _pending.Clear();
            _game.Dispose();
        }
    }
}
