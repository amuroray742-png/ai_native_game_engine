#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Network
{
    internal sealed class TcpMessageConnection : IMessageConnection
    {
        private const int ReceiveQueueCapacity = 16;
        private const int DispatchBudget = 16;
        private const int ReplyDrainSeconds = 2;

        private readonly Socket _socket;
        private readonly Thread _receiver;
        private readonly Thread _sender;
        private readonly object _gate = new object();
        private readonly ConcurrentQueue<ArraySegment<byte>> _toWrite = new ConcurrentQueue<ArraySegment<byte>>();
        private readonly AutoResetEvent _wakeSender = new AutoResetEvent(false);
        private const int WriterIdle = 0, WriterActive = 1, WriterStopped = 2;
        private int _writerState;
        private int _sleeping;
        // The sender publishes cumulative successful writes; only the main thread dispatches them.
        private long _written, _writtenBytes;
        private long _notified, _submittedBytes;
        private long? _backlogSince;
        private long _nextWarning;
        private readonly string _peer;
        private readonly ArrayPool<byte>? _bufferPool;
        private TcpSmallSendPool? _smallBuffers;
        private TcpWriteBatch? _batch;
        private readonly ConcurrentQueue<ArraySegment<byte>> _incoming = new ConcurrentQueue<ArraySegment<byte>>();
        // The receiver owns the produced totals. Only the main thread releases capacity.
        private long _receivedFrames, _receivedBytes;
        private long _consumedFrames, _consumedBytes;
        private int _waitingForRoom;
        private bool _peerFinishedSending;
        private long? _replyDeadline;
        private DisconnectReason? _terminal;
        private Exception? _failure;
        private readonly int _maximum;
        private readonly int _warnPendingSends;
        private readonly long _warnPendingSendBytes;
        private readonly CancellationToken _lifetime;
        private readonly Action<TcpMessageConnection, Task> _retired;
        private readonly Action<TcpMessageConnection> _released;
        private readonly ITimeTicket _update;
        private readonly Queue<UniTaskCompletionSource> _sends = new Queue<UniTaskCompletionSource>();
        private readonly byte[] _readHeader = new byte[TcpFrames.HeaderSize];
        private MessageReceivedHandler? _messageReceived;
        private Delegate[] _messageListeners = Array.Empty<Delegate>();
        private Task? _drain;
        private TaskCompletionSource<bool>? _settled;
        private CancellationTokenSource? _settlementCancellation;
        private bool _notifying, _settling, _forced;
        private long _forcedWrites;

        internal TcpMessageConnection(Socket socket, int maximum, int maxPendingSends, long maxPendingSendBytes,
            ITimeMachine time, CancellationToken lifetime, Action<TcpMessageConnection, Task> retired, Action<TcpMessageConnection> released,
            ArrayPool<byte>? bufferPool, Action<Thread>? startThread = null)
        {
            _socket = socket;
            _peer = socket.RemoteEndPoint!.ToString();
            _bufferPool = bufferPool;
            _maximum = maximum;
            _warnPendingSends = maxPendingSends;
            _warnPendingSendBytes = maxPendingSendBytes;
            _lifetime = lifetime;
            _retired = retired;
            _released = released;
            _receiver = new Thread(ReceiveLoop) { Name = "Network.TCP.Receive", IsBackground = true };
            _sender = new Thread(SendLoop) { Name = "Network.TCP.Send", IsBackground = true };
            _update = time.RegisterUpdate(Pump, "Network.TCP");
            bool senderStarted = false;
            try
            {
                if (startThread == null) { _sender.Start(); } else { startThread(_sender); }
                senderStarted = true;
                if (startThread == null) { _receiver.Start(); } else { startThread(_receiver); }
            }
            catch
            {
                _update.Unregister();
                StopWorkers(DisconnectReason.TransportError);
                _drain = Task.Run(() =>
                {
                    if (senderStarted) { _sender.Join(); }
                    _wakeSender.Dispose();
                });
                _ = TcpFrames.DrainAsync(_drain);
                _retired(this, _drain);
                throw;
            }
        }

        public event MessageReceivedHandler? MessageReceived
        {
            add
            {
                _messageReceived += value;
                _messageListeners = _messageReceived?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
            remove
            {
                _messageReceived -= value;
                _messageListeners = _messageReceived?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }
        public event Action<DisconnectReason>? Disconnected;

        public UniTask SendAsync(byte[] message)
        {
            if (_drain != null || _replyDeadline.HasValue) { throw new ObjectDisposedException(nameof(TcpMessageConnection)); }
            if (message == null) { throw new ArgumentNullException(nameof(message)); }
            if (message.Length > _maximum) { throw new ArgumentOutOfRangeException(nameof(message)); }
            var completion = new UniTaskCompletionSource();
            if (Volatile.Read(ref _writerState) != WriterStopped)
            {
                var pool = _bufferPool;
                if (message.Length <= TcpSmallSendPool.FrameSize - TcpFrames.HeaderSize)
                {
                    pool = _smallBuffers ??= new TcpSmallSendPool(_bufferPool ?? ArrayPool<byte>.Shared);
                }
                var frame = TcpFrames.Encode(message, pool);
                _submittedBytes += frame.Count;
                _toWrite.Enqueue(frame);
                if (Volatile.Read(ref _sleeping) != 0) { _wakeSender.Set(); }
            }
            // Only the main thread submits and closes. A concurrent worker failure is
            // published on this thread before its outstanding submissions are settled.
            _sends.Enqueue(completion);
            return completion.Task;
        }

        private void ReceiveLoop()
        {
            try
            {
                while (WaitForRoom(0))
                {
                    int? length = TcpFrames.ReadLength(_socket, _maximum, _readHeader);
                    if (!length.HasValue)
                    {
                        lock (_gate) { _peerFinishedSending = true; }
                        return;
                    }
                    if (!WaitForRoom(length.Value)) { return; }
                    var payload = TcpFrames.ReadPayload(_socket, length.Value, _bufferPool);
                    if (Volatile.Read(ref _writerState) == WriterStopped) { TcpFrames.Return(payload, _bufferPool); return; }
                    _receivedFrames++;
                    _receivedBytes += payload.Count;
                    _incoming.Enqueue(payload);
                }
            }
            catch (InvalidDataException error) { StopWorkers(DisconnectReason.ProtocolError, error); }
            catch (Exception error) { StopWorkers(DisconnectReason.TransportError, error); }
        }

        private bool HasRoom(int length) =>
            _receivedFrames - Volatile.Read(ref _consumedFrames) < ReceiveQueueCapacity &&
            length <= _maximum - (_receivedBytes - Volatile.Read(ref _consumedBytes));

        private bool WaitForRoom(int length)
        {
            if (HasRoom(length)) { return Volatile.Read(ref _writerState) != WriterStopped; }
            lock (_gate)
            {
                // Publish the waiter before checking capacity again. The consumer publishes
                // capacity before checking this flag, so neither side can miss the handoff.
                Interlocked.Exchange(ref _waitingForRoom, 1);
                try
                {
                    while (!_terminal.HasValue && !HasRoom(length)) { Monitor.Wait(_gate); }
                    return !_terminal.HasValue;
                }
                finally { Volatile.Write(ref _waitingForRoom, 0); }
            }
        }

        private void SendLoop()
        {
            Func<byte[], int, int, int> send = (bytes, offset, count) =>
                _socket.Send(bytes, offset, count, SocketFlags.None);
            Action<int, int> publish = PublishWrites;
            ArraySegment<byte> next = default;
            try
            {
                while (Volatile.Read(ref _writerState) != WriterStopped)
                {
                    if (next.Array == null && !_toWrite.TryDequeue(out next))
                    {
                        Interlocked.Exchange(ref _sleeping, 1);
                        if (_toWrite.IsEmpty && Volatile.Read(ref _writerState) != WriterStopped) { _wakeSender.WaitOne(); }
                        Volatile.Write(ref _sleeping, 0);
                        continue;
                    }
                    if (Interlocked.CompareExchange(ref _writerState, WriterActive, WriterIdle) != WriterIdle) { break; }
                    try { WriteAvailable(ref next, send, publish); }
                    catch (Exception error) { StopWorkers(DisconnectReason.TransportError, error); return; }
                    finally { Interlocked.CompareExchange(ref _writerState, WriterIdle, WriterActive); }
                }
            }
            finally { if (next.Array != null) { RecycleWrittenBuffer(next); } }
        }

        private void PublishWrites(int frames, int bytes)
        {
            Volatile.Write(ref _writtenBytes, _writtenBytes + bytes);
            Volatile.Write(ref _written, _written + frames);
        }

        private void RecycleWrittenBuffer(ArraySegment<byte> frame)
        {
            TcpFrames.Return(frame, frame.Count <= TcpSmallSendPool.FrameSize ? _smallBuffers : _bufferPool);
        }

        private void WriteAvailable(ref ArraySegment<byte> next, Func<byte[], int, int, int> send, Action<int, int> publish)
        {
            var first = next;
            next = default;
            try
            {
                if (first.Count > TcpSmallSendPool.FrameSize || !_toWrite.TryDequeue(out next) ||
                    next.Count > TcpSmallSendPool.FrameSize)
                {
                    TcpFrames.Write(first, send);
                    publish(1, first.Count);
                    return;
                }
                _batch ??= new TcpWriteBatch();
                _batch.Reset();
                _batch.Append(first);
            }
            finally { RecycleWrittenBuffer(first); }

            // A large lookahead remains in next, ahead of every frame still in the queue.
            do
            {
                _batch.Append(next);
                RecycleWrittenBuffer(next);
                next = default;
            }
            while (_batch.Count < TcpWriteBatch.MaxFrames && _toWrite.TryDequeue(out next) &&
                next.Count <= TcpSmallSendPool.FrameSize);
            _batch.Write(send, publish);
        }

        private void StopWorkers(DisconnectReason reason, Exception? cause = null)
        {
            lock (_gate)
            {
                if (_terminal.HasValue) { return; }
                Interlocked.Exchange(ref _writerState, WriterStopped);
                _wakeSender.Set();
                _terminal = reason;
                _failure = cause;
                try { _socket.Shutdown(SocketShutdown.Both); }
                catch (SocketException) { /* The peer may already have disconnected. */ }
                finally
                {
                    _socket.Dispose();
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void Pump(float delta)
        {
            SampleBacklog(Stopwatch.GetTimestamp());
            long written = Volatile.Read(ref _written);
            for (int i = 0; i < DispatchBudget && _drain == null && _notified < written; i++)
            {
                if (Volatile.Read(ref _writerState) == WriterStopped) { break; }
                _notified++;
                _sends.Dequeue().TrySetResult();
            }
            for (int i = 0; i < DispatchBudget && _drain == null; i++)
            {
                if (!_incoming.TryDequeue(out var payload)) { break; }
                Volatile.Write(ref _consumedBytes, _consumedBytes + payload.Count);
                Interlocked.Increment(ref _consumedFrames);
                if (Volatile.Read(ref _waitingForRoom) != 0)
                {
                    lock (_gate) { Monitor.PulseAll(_gate); }
                }
                try { DispatchMessage(payload.AsSpan()); }
                finally { TcpFrames.Return(payload, _bufferPool); }
            }
            if (_drain == null) { CheckReceiveEnd(); }
        }

        private void SampleBacklog(long now)
        {
            long bytes = _submittedBytes - Volatile.Read(ref _writtenBytes);
            if (_sends.Count <= _warnPendingSends && bytes <= _warnPendingSendBytes)
            {
                _backlogSince = null;
                return;
            }
            if (!_backlogSince.HasValue)
            {
                _backlogSince = now;
                _nextWarning = now + Stopwatch.Frequency;
            }
            if (now < _nextWarning) { return; }
            _nextWarning = now + 10 * Stopwatch.Frequency;
            long completed = Volatile.Read(ref _written) - _notified;
            double seconds = (now - _backlogSince.Value) / (double)Stopwatch.Frequency;
            Debug.LogWarning($"TCP send backlog peer={_peer} pendingFrames={_sends.Count - completed} " +
                $"pendingBytes={bytes} writtenAwaitingDispatch={completed} durationSeconds={seconds:F1}");
        }

        private void CheckReceiveEnd()
        {
            DisconnectReason reason;
            lock (_gate)
            {
                if (!_incoming.IsEmpty) { return; }
                if (_terminal.HasValue) { reason = _terminal.Value; }
                else
                {
                    if (!_peerFinishedSending) { return; }
                    // All final receive callbacks have returned. Seal admission and let their
                    // replies finish, even when the peer reads them on a later frame.
                    long now = Stopwatch.GetTimestamp();
                    _replyDeadline ??= now + ReplyDrainSeconds * Stopwatch.Frequency;
                    if ((_submittedBytes != Volatile.Read(ref _writtenBytes)) && now < _replyDeadline.Value) { return; }
                    reason = DisconnectReason.RemoteClose;
                }
            }
            Stop(reason);
        }

        private void DispatchMessage(ReadOnlySpan<byte> payload)
        {
            foreach (MessageReceivedHandler listener in _messageListeners)
            {
                if (_drain != null) { break; }
                try { listener(payload); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }

        public async UniTask CloseAsync()
        {
            var drain = Stop(DisconnectReason.LocalClose);
            if (!drain.IsCompleted)
            {
                await UniTask.WaitUntil(() => drain.IsCompleted,
                    cancellationToken: _lifetime, cancelImmediately: true);
            }
            drain.GetAwaiter().GetResult();
        }

        internal Task Stop(DisconnectReason reason, Exception? cause = null)
        {
            if (_drain != null) { return _drain; }
            _update.Unregister();
            StopWorkers(reason, cause);
            var workers = Task.Run(ReclaimWorkers);
            _settled = new TaskCompletionSource<bool>();
            _settlementCancellation = new CancellationTokenSource();
            // Publish retirement before invoking subscribers: they may restart or retire Core inline.
            _drain = Task.WhenAll(workers, _settled.Task);
            _ = TcpFrames.DrainAsync(_drain);
            _retired(this, _drain);
            var listeners = Disconnected;
            Disconnected = null;
            _messageReceived = null;
            _messageListeners = Array.Empty<Delegate>();
            _notifying = true;
            try
            {
                if (listeners != null)
                {
                    foreach (Action<DisconnectReason> listener in listeners.GetInvocationList())
                    {
                        if (_forced) { break; }
                        try { listener(_terminal!.Value); }
                        catch (Exception error) { Debug.LogException(error); }
                    }
                }
            }
            finally
            {
                _notifying = false;
                if (_forced) { SettleSends(); }
                else { SettleAfterWorkersAsync(workers).Forget(); }
            }
            return _drain;
        }

        private void ReclaimWorkers()
        {
            _sender.Join();
            _smallBuffers?.Drain();
            _receiver.Join();
            while (_toWrite.TryDequeue(out var frame)) { TcpFrames.Return(frame, _bufferPool); }
            while (_incoming.TryDequeue(out var payload)) { TcpFrames.Return(payload, _bufferPool); }
            _wakeSender.Dispose();
        }

        private async UniTask SettleAfterWorkersAsync(Task workers)
        {
            try
            {
                // Core's dispatcher is already stopped during shutdown. UniTask's PlayerLoop
                // still runs, independently of module shutdown order and the run token.
                await UniTask.WaitUntil(() => workers.IsCompleted,
                    cancellationToken: _settlementCancellation!.Token, cancelImmediately: true);
            }
            catch (OperationCanceledException) when (_forced) { return; }
            if (workers.IsFaulted) { _failure ??= workers.Exception; }
            SettleSends();
        }

        internal void ForceStop()
        {
            Stop(DisconnectReason.ModuleShutdown);
            // A constructor failure has no admitted sends or business callbacks.
            if (_settled == null || _settled.Task.IsCompleted || _forced) { return; }
            _forcedWrites = Volatile.Read(ref _written);
            _forced = true;
            _settlementCancellation!.Cancel();
            if (!_notifying) { SettleSends(); }
        }

        private void SettleSends()
        {
            if (_settling || _settled!.Task.IsCompleted) { return; }
            _settling = true;
            try
            {
                while (_sends.Count > 0)
                {
                    var completion = _sends.Dequeue();
                    long completedWrites = _forced ? _forcedWrites : Volatile.Read(ref _written);
                    bool written = _notified++ < completedWrites;
                    if (written) { completion.TrySetResult(); }
                    else if (_forced) { completion.TrySetCanceled(); }
                    else { completion.TrySetException(_failure ?? new IOException("TCP connection closed: " + _terminal)); }
                }
            }
            finally
            {
                _settlementCancellation!.Dispose();
                _settled.TrySetResult(true);
                _released(this);
            }
        }
    }
}
