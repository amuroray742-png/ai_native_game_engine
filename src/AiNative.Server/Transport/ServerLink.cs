using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Actors;
using AiNative.Server.Configuration;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Protocol.Serialization;

namespace AiNative.Server.Transport
{
    /// <summary>
    /// 一个已认证进程连接的唯一读写所有者。不自动重连/重放，不执行玩法，不把投递或发送成功当业务提交。
    /// deliver/rejected 必须同步、非阻塞，只投递本地消息；每次回调独占已解码 DTO。
    /// </summary>
    public sealed class ServerLink : IDisposable
    {
        private readonly AuthenticatedServerStream _connection;
        private readonly ServerReceiveContext _context;
        private readonly ServerBudgets _budgets;
        private readonly FramedStream _frames;
        private readonly BoundedSendQueue _send;
        private readonly CancellationTokenSource _abort = new CancellationTokenSource();
        private readonly CancellationToken _abortToken;
        private int _started;
        private int _disposed;

        public ServerLink(AuthenticatedServerStream connection, ServerReceiveContext context, ServerBudgets budgets)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(budgets);
            budgets.Validate();
            if (context.AllowRegistration || context.ControlEpoch == null || context.ControlEpoch == Guid.Empty)
            {
                throw new ArgumentException("ServerLink 必须在宿主确认控制 epoch 后建立，不保留空 epoch 注册例外。");
            }
            if (connection.Remote.Peer != context.Remote) { throw new ArgumentException("接收上下文必须使用认证对端身份。"); }
            _connection = connection;
            _context = context;
            _budgets = budgets;
            _abortToken = _abort.Token;
            _frames = new FramedStream(connection.Stream, budgets.MaxFrameBytes,
                new FrameBudget(budgets.ReceiveFrames, budgets.ReceiveBytes), new FrameBudget(1, budgets.MaxFrameBytes));
            _send = new BoundedSendQueue(budgets.Send);
        }

        /// <summary>只在循环已启动后接纳。route 消息沿同一 roomId 队列，路由无关消息走控制额度。</summary>
        public SendAdmission TrySend(string type, Guid operationId, JsonElement payload, out Task? written)
        {
            written = null;
            if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _disposed) != 0) { return SendAdmission.Stopped; }
            byte[] bytes = ServerCodec.Encode(type, operationId, _context.Local, _context.Remote,
                _context.ControlEpoch, payload, _context.ControlEpoch);
            if (bytes.Length > _budgets.MaxFrameBytes) { throw new ArgumentException("消息超出本连接配置的帧上限。"); }
            if (payload.TryGetProperty("route", out JsonElement route) && route.ValueKind == JsonValueKind.Object)
            {
                return _send.TryEnqueueRoute(route.GetProperty("roomId").GetGuid(), bytes, out written);
            }
            SendAdmission result = _send.TryEnqueueControl(bytes, out written);
            // 控制额度失败不能静默丢失监督/心跳；故障交给进程监督者。
            if (result == SendAdmission.Full) { Dispose(); }
            return result;
        }

        /// <summary>停止发送接纳，排空后关闭整条连接；调用方等待 RunAsync，期限到则 Dispose 中断。</summary>
        public void CompleteSending() => _send.Complete();

        public async Task RunAsync(Func<ServerMessage, PostResult> deliver,
            Action<ServerMessage, PostResult> rejected, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(deliver);
            ArgumentNullException.ThrowIfNull(rejected);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) { throw new InvalidOperationException("ServerLink 只能运行一次。"); }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortToken);
            Task reader = ReadLoopAsync(deliver, rejected, lifetime.Token);
            Task writer = _send.RunAsync(_frames, lifetime.Token, TimeSpan.FromSeconds(_budgets.WriteSeconds));
            Exception? failure = null;
            Task first = await Task.WhenAny(reader, writer).ConfigureAwait(false);
            try { await first.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            finally
            {
                lifetime.Cancel();
                Dispose();
                try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (failure == null && error is not OperationCanceledException) { failure = error; }
                }
            }
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            _send.Complete();
            _abort.Cancel();
            _connection.Dispose();
            _abort.Dispose();
        }

        private async Task ReadLoopAsync(Func<ServerMessage, PostResult> deliver,
            Action<ServerMessage, PostResult> rejected, CancellationToken cancellationToken)
        {
            int turnFrames = 0;
            int turnBytes = 0;
            while (true)
            {
                using ReceivedFrame? frame = await _frames.ReadAsync(cancellationToken,
                    TimeSpan.FromSeconds(_budgets.ReadSeconds), TimeSpan.FromSeconds(_budgets.FrameSeconds)).ConfigureAwait(false);
                if (frame == null) { return; }
                if (!ServerCodec.TryDecode(frame.Body, _context, out ServerMessage? message, out ServerMessageError error))
                {
                    throw new InvalidDataException($"内部消息拒绝：{error}。");
                }
                PostResult admission = deliver(message!);
                if (admission != PostResult.Accepted) { rejected(message!, admission); }
                turnFrames++;
                turnBytes += frame.Body.Length;
                if (turnFrames >= 32 || turnBytes >= 64 * 1024)
                {
                    turnFrames = 0;
                    turnBytes = 0;
                    await Task.Yield();
                }
            }
        }
    }
}
