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

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly int _maximum;
        private readonly CancellationToken _lifetime;
        private readonly Action<TcpMessageConnection, Task> _retired;
        private readonly ITimeTicket _update;
        private readonly Queue<Send> _sends = new Queue<Send>();
        private Task<byte[]>? _read;
        private Task? _write;
        private Task? _drain;

        internal TcpMessageConnection(TcpClient client, int maximum, ITimeMachine time, CancellationToken lifetime,
            Action<TcpMessageConnection, Task> retired)
        {
            _client = client;
            _stream = client.GetStream();
            _maximum = maximum;
            _lifetime = lifetime;
            _retired = retired;
            _update = time.RegisterUpdate(Pump, "Network.TCP");
        }

        public event Action<byte[]>? MessageReceived;
        public event Action<DisconnectReason>? Disconnected;

        public UniTask SendAsync(byte[] message)
        {
            if (_drain != null) { throw new ObjectDisposedException(nameof(TcpMessageConnection)); }
            if (message == null) { throw new ArgumentNullException(nameof(message)); }
            if (message.Length > _maximum) { throw new ArgumentOutOfRangeException(nameof(message)); }
            if (_sends.Count >= 16) { throw new InvalidOperationException("TCP send queue is full."); }
            var send = new Send(TcpFrames.Encode(message));
            _sends.Enqueue(send);
            return send.Completion.Task;
        }

        private void Pump(float delta)
        {
            if (_drain != null) { return; }
            try
            {
                // Cap synchronous completions per frame. A slow consumer holds at most one incoming frame.
                for (int i = 0; i < 16 && _drain == null && _sends.Count > 0; i++)
                {
                    var current = _sends.Peek();
                    _write ??= _stream.WriteAsync(current.Frame, 0, current.Frame.Length);
                    if (!_write.IsCompleted) { break; }
                    _write.GetAwaiter().GetResult();
                    _write = null;
                    _sends.Dequeue();
                    current.Completion.TrySetResult();
                }
                for (int i = 0; i < 16 && _drain == null; i++)
                {
                    _read ??= TcpFrames.ReadAsync(_stream, _maximum);
                    if (!_read.IsCompleted) { break; }
                    var payload = _read.GetAwaiter().GetResult();
                    _read = null;
                    if (payload == null) { Stop(DisconnectReason.RemoteClose); return; }
                    var listeners = MessageReceived;
                    if (listeners == null) { continue; }
                    foreach (Action<byte[]> listener in listeners.GetInvocationList())
                    {
                        if (_drain != null) { break; }
                        try { listener(payload); }
                        catch (Exception error) { Debug.LogException(error); }
                    }
                }
            }
            catch (InvalidDataException error) { Stop(DisconnectReason.ProtocolError, error); }
            catch (IOException error) { Stop(DisconnectReason.TransportError, error); }
            catch (SocketException error) { Stop(DisconnectReason.TransportError, error); }
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
            _client.Dispose();
            var notified = new TaskCompletionSource<bool>();
            var io = TcpFrames.DrainAsync(Task.WhenAll(_read ?? Task.CompletedTask, _write ?? Task.CompletedTask));
            // A subscriber can restart Core inline. Keep this connection in shutdown's
            // drain set until every old subscriber and pending send has been notified.
            _drain = Task.WhenAll(io, notified.Task);
            _retired(this, _drain);
            var listeners = Disconnected;
            Disconnected = null;
            MessageReceived = null;
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
                    _sends.Dequeue().Completion.TrySetException(cause ?? new IOException("TCP connection closed: " + reason));
                }
            }
            finally { notified.SetResult(true); }
            return _drain;
        }
    }
}
