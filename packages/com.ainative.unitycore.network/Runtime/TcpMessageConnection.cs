#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Network
{
    internal sealed class TcpMessageConnection : IMessageConnection
    {
        private sealed class Send
        {
            internal readonly byte[] Frame;
            internal readonly UniTaskCompletionSource Completion = new UniTaskCompletionSource();
            internal Send(byte[] frame) { Frame = frame; }
        }

        private readonly Socket _socket;
        private readonly Thread _receiver;
        private readonly Thread _sender;
        private readonly object _gate = new object();
        // Worker handoff under _gate. The main thread owns _sends and all user callbacks.
        private readonly Queue<byte[]> _toWrite = new Queue<byte[]>();
        private byte[]? _incoming;
        private int _written;
        private DisconnectReason? _terminal;
        private Exception? _failure;
        private readonly int _maximum;
        private readonly CancellationToken _lifetime;
        private readonly Action<TcpMessageConnection, Task> _retired;
        private readonly ITimeTicket _update;
        private readonly Queue<Send> _sends = new Queue<Send>();
        private readonly byte[] _readHeader = new byte[4];
        private Action<byte[]>? _messageReceived;
        private Delegate[] _messageListeners = Array.Empty<Delegate>();
        private Task? _drain;

        internal TcpMessageConnection(Socket socket, int maximum, ITimeMachine time, CancellationToken lifetime,
            Action<TcpMessageConnection, Task> retired)
        {
            _socket = socket;
            _maximum = maximum;
            _lifetime = lifetime;
            _retired = retired;
            _receiver = new Thread(ReceiveLoop) { Name = "Network.TCP.Receive", IsBackground = true };
            _sender = new Thread(SendLoop) { Name = "Network.TCP.Send", IsBackground = true };
            _update = time.RegisterUpdate(Pump, "Network.TCP");
            bool receiverStarted = false;
            try
            {
                _receiver.Start();
                receiverStarted = true;
                _sender.Start();
            }
            catch
            {
                _update.Unregister();
                StopWorkers(DisconnectReason.TransportError);
                if (receiverStarted) { _retired(this, Task.Run(() => _receiver.Join())); }
                throw;
            }
        }

        public event Action<byte[]>? MessageReceived
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
            if (_drain != null) { throw new ObjectDisposedException(nameof(TcpMessageConnection)); }
            if (message == null) { throw new ArgumentNullException(nameof(message)); }
            if (message.Length > _maximum) { throw new ArgumentOutOfRangeException(nameof(message)); }
            if (_sends.Count >= 16) { throw new InvalidOperationException("TCP send queue is full."); }
            var send = new Send(TcpFrames.Encode(message));
            lock (_gate)
            {
                _sends.Enqueue(send);
                // Until the main thread publishes disconnect, settle new submissions with
                // the other pending sends after the disconnect callback.
                if (!_terminal.HasValue)
                {
                    _toWrite.Enqueue(send.Frame);
                    Monitor.PulseAll(_gate);
                }
            }
            return send.Completion.Task;
        }

        private void ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    lock (_gate)
                    {
                        while (_incoming != null && !_terminal.HasValue) { Monitor.Wait(_gate); }
                        if (_terminal.HasValue) { return; }
                    }
                    var payload = TcpFrames.Read(_socket, _maximum, _readHeader);
                    if (payload == null) { StopWorkers(DisconnectReason.RemoteClose); return; }
                    lock (_gate)
                    {
                        if (_terminal.HasValue) { return; }
                        _incoming = payload;
                    }
                }
            }
            catch (InvalidDataException error) { StopWorkers(DisconnectReason.ProtocolError, error); }
            catch (Exception error) { StopWorkers(DisconnectReason.TransportError, error); }
        }

        private void SendLoop()
        {
            try
            {
                Func<byte[], int, int, int> send = (bytes, offset, count) =>
                    _socket.Send(bytes, offset, count, SocketFlags.None);
                while (true)
                {
                    byte[] frame;
                    lock (_gate)
                    {
                        while (_toWrite.Count == 0 && !_terminal.HasValue) { Monitor.Wait(_gate); }
                        if (_terminal.HasValue) { return; }
                        frame = _toWrite.Dequeue();
                    }
                    TcpFrames.Write(frame, send);
                    lock (_gate) { _written++; }
                }
            }
            catch (Exception error) { StopWorkers(DisconnectReason.TransportError, error); }
        }

        private void StopWorkers(DisconnectReason reason, Exception? cause = null)
        {
            lock (_gate)
            {
                if (_terminal.HasValue) { return; }
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
            for (int i = 0; i < 16 && _drain == null; i++)
            {
                lock (_gate)
                {
                    if (_written == 0 || _terminal.HasValue) { break; }
                    _written--;
                }
                _sends.Dequeue().Completion.TrySetResult();
            }
            for (int i = 0; i < 16 && _drain == null; i++)
            {
                byte[]? payload;
                DisconnectReason? reason;
                Exception? failure;
                lock (_gate)
                {
                    reason = _terminal;
                    failure = _failure;
                    payload = _incoming;
                }
                if (reason.HasValue) { Stop(reason.Value, failure); return; }
                if (payload == null) { break; }
                DispatchMessage(payload);
                // The callback can reply to this frame before we look for EOF.
                lock (_gate)
                {
                    _incoming = null;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void DispatchMessage(byte[] payload)
        {
            foreach (Action<byte[]> listener in _messageListeners)
            {
                if (_drain != null) { break; }
                try { listener(payload); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }

        public UniTask CloseAsync()
        {
            var drain = Stop(DisconnectReason.LocalClose);
            return drain.IsCompleted ? UniTask.CompletedTask : UniTask.WaitUntil(() => drain.IsCompleted,
                cancellationToken: _lifetime, cancelImmediately: true);
        }

        internal Task Stop(DisconnectReason reason, Exception? cause = null)
        {
            if (_drain != null) { return _drain; }
            _update.Unregister();
            StopWorkers(reason, cause);
            int completedWrites;
            lock (_gate)
            {
                reason = _terminal!.Value;
                cause = _failure;
                completedWrites = reason == DisconnectReason.LocalClose || reason == DisconnectReason.ModuleShutdown
                    ? 0 : _written;
                _incoming = null;
                _toWrite.Clear();
            }
            var notified = new TaskCompletionSource<bool>();
            var workers = Task.Run(() =>
            {
                _sender.Join();
                _receiver.Join();
            });
            // A subscriber can restart Core inline. Keep this connection in shutdown's
            // drain set until every old subscriber and pending send has been notified.
            _drain = Task.WhenAll(workers, notified.Task);
            _retired(this, _drain);
            var listeners = Disconnected;
            Disconnected = null;
            _messageReceived = null;
            _messageListeners = Array.Empty<Delegate>();
            // UniTask resumes waiting callers inline. Publish the terminal reason before a
            // failed writer can resume and treat an already-drained socket as fully closed.
            try
            {
                if (listeners != null)
                {
                    foreach (Action<DisconnectReason> listener in listeners.GetInvocationList())
                    {
                        try { listener(reason); }
                        catch (Exception error) { Debug.LogException(error); }
                    }
                }
                while (_sends.Count > 0)
                {
                    var completion = _sends.Dequeue().Completion;
                    if (completedWrites > 0)
                    {
                        completedWrites--;
                        completion.TrySetResult();
                    }
                    else { completion.TrySetException(cause ?? new IOException("TCP connection closed: " + reason)); }
                }
            }
            finally { notified.SetResult(true); }
            return _drain;
        }
    }
}
