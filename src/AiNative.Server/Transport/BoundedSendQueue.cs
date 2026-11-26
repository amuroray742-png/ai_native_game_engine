using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Server.Transport
{
    public enum SendAdmission { Accepted, Full, Stopped }

    /// <summary>
    /// 单写循环：独占正文副本至实际写入结算；控制额度独立，房间路由按字节轮转且各自 FIFO。
    /// Accepted 仅表示接纳，written 仅表示本地完整写入。连接所有者负责 I/O 期限与关闭流。
    /// </summary>
    public sealed class BoundedSendQueue
    {
        private sealed class Entry
        {
            internal readonly byte[] Body;
            internal readonly TaskCompletionSource Written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Entry(byte[] body) { Body = body; }
        }

        private sealed class Route
        {
            internal readonly Guid Id;
            internal readonly Queue<Entry> Entries = new Queue<Entry>();
            internal int Frames;
            internal long Bytes;
            internal long Deficit;
            internal Route(Guid id) { Id = id; }
        }

        private readonly object _gate = new object();
        private readonly SendQueueLimits _limits;
        private readonly Queue<Entry> _control = new Queue<Entry>();
        private readonly Dictionary<Guid, Route> _routes = new Dictionary<Guid, Route>();
        private readonly LinkedList<Route> _ready = new LinkedList<Route>();
        private TaskCompletionSource? _changed;
        private int _controlFrames;
        private long _controlBytes;
        private int _dataFrames;
        private long _dataBytes;
        private int _controlRun;
        private bool _stopped;
        private bool _started;

        public BoundedSendQueue(SendQueueLimits limits)
        {
            ArgumentNullException.ThrowIfNull(limits);
            limits.Validate();
            _limits = limits;
        }

        /// <summary>只用于与房间无关的心跳/监督消息；房间绑定、基线和状态必须走同一路由。</summary>
        public SendAdmission TryEnqueueControl(ReadOnlySpan<byte> body, out Task? written) => Enqueue(null, body, out written);

        public SendAdmission TryEnqueueRoute(Guid routeId, ReadOnlySpan<byte> body, out Task? written) => Enqueue(routeId, body, out written);

        /// <summary>停止接纳，已经接纳的帧继续排空。取消 RunAsync 则中断并失败结算剩余帧。</summary>
        public void Complete()
        {
            TaskCompletionSource? changed;
            lock (_gate)
            {
                _stopped = true;
                changed = _changed;
                _changed = null;
            }
            changed?.TrySetResult();
        }

        public async Task RunAsync(FramedStream stream, CancellationToken cancellationToken = default, TimeSpan? writeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (writeTimeout.HasValue && writeTimeout.Value <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(writeTimeout)); }
            lock (_gate)
            {
                if (_started) { throw new InvalidOperationException("只能启动一个发送循环。"); }
                _started = true;
            }
            Entry? current = null;
            Route? route = null;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Task? wait = null;
                    lock (_gate)
                    {
                        (current, route) = Take();
                        if (current == null)
                        {
                            if (_stopped) { return; }
                            _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            wait = _changed.Task;
                        }
                    }
                    if (wait != null)
                    {
                        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (writeTimeout.HasValue) { deadline.CancelAfter(writeTimeout.Value); }
                    await stream.WriteAsync(current!.Body, deadline.Token).ConfigureAwait(false);
                    lock (_gate) { Release(current, route); }
                    current.Written.TrySetResult();
                    current = null;
                }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _stopped = true;
                    if (current != null)
                    {
                        Release(current, route);
                        current.Written.TrySetException(error);
                    }
                    while (_control.TryDequeue(out Entry? pending)) { Release(pending, null); pending.Written.TrySetException(error); }
                    foreach (Route pendingRoute in _routes.Values)
                    {
                        while (pendingRoute.Entries.TryDequeue(out Entry? pending)) { pending.Written.TrySetException(error); }
                    }
                    _routes.Clear();
                    _ready.Clear();
                    _dataFrames = 0;
                    _dataBytes = 0;
                }
                throw;
            }
        }

        private SendAdmission Enqueue(Guid? routeId, ReadOnlySpan<byte> body, out Task? written)
        {
            if (body.Length == 0) { throw new ArgumentException("发送正文不能为空。", nameof(body)); }
            written = null;
            TaskCompletionSource? changed;
            lock (_gate)
            {
                if (_stopped) { return SendAdmission.Stopped; }
                Route? route = null;
                if (routeId.HasValue)
                {
                    _routes.TryGetValue(routeId.Value, out route);
                    if (_dataFrames >= _limits.MaxFrames - _limits.ControlFrames || body.Length > _limits.MaxBytes - _limits.ControlBytes - _dataBytes ||
                        (route != null && (route.Frames >= _limits.RouteFrames || body.Length > _limits.RouteBytes - route.Bytes)) || body.Length > _limits.RouteBytes)
                    { return SendAdmission.Full; }
                }
                else if (_controlFrames >= _limits.ControlFrames || body.Length > _limits.ControlBytes - _controlBytes) { return SendAdmission.Full; }
                var entry = new Entry(body.ToArray());
                if (routeId.HasValue)
                {
                    if (route == null) { route = new Route(routeId.Value); _routes.Add(route.Id, route); }
                    if (route.Entries.Count == 0) { _ready.AddLast(route); }
                    route.Entries.Enqueue(entry);
                    route.Frames++;
                    route.Bytes += body.Length;
                    _dataFrames++;
                    _dataBytes += body.Length;
                }
                else
                {
                    _control.Enqueue(entry);
                    _controlFrames++;
                    _controlBytes += body.Length;
                }
                written = entry.Written.Task;
                changed = _changed;
                _changed = null;
            }
            changed?.TrySetResult();
            return SendAdmission.Accepted;
        }

        private (Entry?, Route?) Take()
        {
            if (_control.Count > 0 && (_controlRun < _limits.ControlBurst || _ready.Count == 0))
            {
                if (_controlRun < _limits.ControlBurst) { _controlRun++; }
                return (_control.Dequeue(), null);
            }
            while (_ready.First != null)
            {
                Route route = _ready.First.Value;
                Entry entry = route.Entries.Peek();
                if (route.Deficit < entry.Body.Length)
                {
                    route.Deficit += _limits.QuantumBytes;
                    if (route.Deficit < entry.Body.Length)
                    {
                        _ready.RemoveFirst();
                        _ready.AddLast(route);
                        continue;
                    }
                }
                route.Entries.Dequeue();
                route.Deficit -= entry.Body.Length;
                _ready.RemoveFirst();
                if (route.Entries.Count > 0)
                {
                    if (route.Deficit >= route.Entries.Peek().Body.Length) { _ready.AddFirst(route); }
                    else { _ready.AddLast(route); }
                }
                else { route.Deficit = 0; }
                _controlRun = 0;
                return (entry, route);
            }
            return (null, null);
        }

        private void Release(Entry entry, Route? route)
        {
            if (route == null) { _controlFrames--; _controlBytes -= entry.Body.Length; return; }
            route.Frames--;
            route.Bytes -= entry.Body.Length;
            _dataFrames--;
            _dataBytes -= entry.Body.Length;
            if (route.Frames == 0) { _routes.Remove(route.Id); }
        }
    }
}
