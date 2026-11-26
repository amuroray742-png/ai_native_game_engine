using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Server.Actors
{
    /// <summary>
    /// 房间执行器。工厂、生命周期和消息均在唯一专用线程运行；停止时排空已接纳消息。
    /// 故障停止并丢弃待处理消息，不重启空状态。监督者必须观察 Completion。
    /// </summary>
    public sealed class DedicatedThreadActor<T> where T : notnull
    {
        private readonly object _signal = new object();
        private readonly ActorMailbox<T> _mailbox;
        private readonly Func<IActorBehavior<T>> _factory;
        private readonly Thread _thread;
        private readonly TaskCompletionSource _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _signaled;

        public DedicatedThreadActor(string actorId, int maxMessages, long maxBytes, Func<IActorBehavior<T>> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
            ArgumentNullException.ThrowIfNull(factory);
            _factory = factory;
            _mailbox = new ActorMailbox<T>(maxMessages, maxBytes, Wake);
            Reference = new ActorRef<T>(actorId, _mailbox);
            _thread = new Thread(Run) { IsBackground = true, Name = actorId };
            _thread.Start();
        }

        public ActorRef<T> Reference { get; }
        public Task Started => _started.Task;
        public Task Completion => _completion.Task;

        /// <summary>停止接单；返回不表示线程退出。正常排空调用 StopAsync。</summary>
        public void RequestStop() => _mailbox.Stop(discardPending: false);

        /// <summary>由外部监督者等待清理与真实线程退出。取消只取消等待，不终止线程。</summary>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            RequestStop();
            try
            {
                await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Completion.IsCompleted)
                {
                    _thread.Join();
                }
            }
        }

        private void Run()
        {
            IActorBehavior<T>? behavior = null;
            Exception? failure = null;
            try
            {
                behavior = _factory();
                behavior.OnStart();
                _started.SetResult();
                while (true)
                {
                    if (_mailbox.TryTake(out var entry))
                    {
                        try
                        {
                            behavior.OnMessage(entry.Message);
                        }
                        finally
                        {
                            _mailbox.Release(entry.Bytes);
                        }
                    }
                    else if (_mailbox.IsDrained)
                    {
                        break;
                    }
                    else
                    {
                        lock (_signal)
                        {
                            while (!_signaled)
                            {
                                Monitor.Wait(_signal);
                            }
                            _signaled = false;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                _started.TrySetException(exception);
            }
            finally
            {
                _mailbox.Stop(discardPending: true);
                try
                {
                    if (behavior != null)
                    {
                        behavior.OnStop();
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

        private void Wake()
        {
            lock (_signal)
            {
                _signaled = true;
                Monitor.Pulse(_signal);
            }
        }
    }
}
