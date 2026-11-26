using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Server.Actors
{
    /// <summary>非房间 Actor 的线程池串行执行器；每轮处理有限消息后归还线程。</summary>
    public sealed class SharedActor<T> where T : notnull
    {
        private readonly ActorMailbox<T> _mailbox;
        private readonly Func<IActorBehavior<T>> _factory;
        private readonly int _messagesPerTurn;
        private readonly TaskCompletionSource _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private IActorBehavior<T>? _behavior;
        private int _scheduled;
        private int _signaled;
        private int _finished;

        public SharedActor(string actorId, int maxMessages, long maxBytes, Func<IActorBehavior<T>> factory, int messagesPerTurn = 32, int reservedControlMessages = 0, long reservedControlBytes = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(messagesPerTurn);
            _factory = factory;
            _messagesPerTurn = messagesPerTurn;
            _mailbox = new ActorMailbox<T>(maxMessages, maxBytes, Wake, reservedControlMessages, reservedControlBytes);
            Reference = new ActorRef<T>(actorId, _mailbox);
            Wake();
        }

        public ActorRef<T> Reference { get; }
        public Task Started => _started.Task;
        public Task Completion => _completion.Task;
        public void RequestStop() => _mailbox.Stop(discardPending: false);

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            RequestStop();
            await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void Wake()
        {
            Volatile.Write(ref _signaled, 1);
            Schedule();
        }

        private void Schedule()
        {
            if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            {
                return;
            }
            // 取得调度权后才读终止状态，避免迟到 Wake 在清理完成后再次运行 OnStop。
            if (Volatile.Read(ref _finished) != 0)
            {
                Volatile.Write(ref _scheduled, 0);
                return;
            }
            ThreadPool.QueueUserWorkItem(_ => RunTurn());
        }

        private void RunTurn()
        {
            Interlocked.Exchange(ref _signaled, 0);
            try
            {
                if (_behavior == null)
                {
                    _behavior = _factory();
                    _behavior.OnStart();
                    _started.SetResult();
                }
                for (int i = 0; i < _messagesPerTurn && _mailbox.TryTake(out var entry); i++)
                {
                    try
                    {
                        _behavior.OnMessage(entry.Message);
                    }
                    finally
                    {
                        _mailbox.Release(entry.Bytes, entry.IsControl);
                    }
                }
                if (_mailbox.IsDrained)
                {
                    Finish(null);
                }
                else if (_mailbox.HasPending)
                {
                    Volatile.Write(ref _signaled, 1);
                }
            }
            catch (Exception exception)
            {
                _started.TrySetException(exception);
                Finish(exception);
            }
            finally
            {
                Volatile.Write(ref _scheduled, 0);
                if (Volatile.Read(ref _signaled) != 0)
                {
                    Schedule();
                }
            }
        }

        private void Finish(Exception? failure)
        {
            Volatile.Write(ref _finished, 1);
            _mailbox.Stop(discardPending: true);
            try
            {
                if (_behavior != null)
                {
                    _behavior.OnStop();
                }
            }
            catch (Exception exception)
            {
                failure = failure == null ? exception : new AggregateException(failure, exception);
            }
            if (failure == null)
            {
                _completion.SetResult();
            }
            else
            {
                _completion.SetException(failure);
            }
        }
    }
}
