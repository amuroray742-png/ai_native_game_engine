using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Server.Transport
{
    /// <summary>
    /// 四字节大端正长度分帧，支持 NetworkStream 或认证后的 SslStream。
    /// 不拥有 Stream；连接宿主负责超时、关闭和协议校验。同方向只允许一个在途操作。
    /// </summary>
    public sealed class FramedStream
    {
        private readonly Stream _stream;
        private readonly int _maxBodyBytes;
        private readonly FrameBudget _receiveBudget;
        private readonly FrameBudget _sendBudget;
        private int _reading;
        private int _writing;
        private int _failed;

        public FramedStream(Stream stream, int maxBodyBytes, FrameBudget receiveBudget, FrameBudget sendBudget)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(receiveBudget);
            ArgumentNullException.ThrowIfNull(sendBudget);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBodyBytes);
            if (maxBodyBytes > receiveBudget.MaxBytes || maxBodyBytes > sendBudget.MaxBytes)
            {
                throw new ArgumentException("预算必须容纳一条最大正文。");
            }
            _stream = stream;
            _maxBodyBytes = maxBodyBytes;
            _receiveBudget = receiveBudget;
            _sendBudget = sendBudget;
        }

        /// <summary>帧边界 EOF 返回 null；截断、超限和预算不足失败后必须关闭连接，禁止续读。</summary>
        public async ValueTask<ReceivedFrame?> ReadAsync(CancellationToken cancellationToken = default)
        {
            Enter(ref _reading);
            int reservedBytes = 0;
            try
            {
                var header = new byte[4];
                int first = await _stream.ReadAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (first == 0)
                {
                    return null;
                }
                await _stream.ReadExactlyAsync(header.AsMemory(first), cancellationToken).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length <= 0 || length > _maxBodyBytes)
                {
                    throw new InvalidDataException("帧正文长度必须为正且不超过配置上限。");
                }
                if (!_receiveBudget.TryReserve(length))
                {
                    throw new IOException("接收正文预算已满，连接必须关闭。");
                }
                reservedBytes = length;
                var body = new byte[length];
                await _stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
                var frame = new ReceivedFrame(body, _receiveBudget);
                reservedBytes = 0;
                return frame;
            }
            catch
            {
                Interlocked.Exchange(ref _failed, 1);
                throw;
            }
            finally
            {
                if (reservedBytes != 0)
                {
                    _receiveBudget.Release(reservedBytes);
                }
                Volatile.Write(ref _reading, 0);
            }
        }

        /// <summary>借用正文直到任务结束，调用方不可修改。完成仅表示本地完整写入，不代表远端执行。</summary>
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        {
            if (body.Length == 0 || body.Length > _maxBodyBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(body));
            }
            Enter(ref _writing);
            bool reserved = false;
            bool writeStarted = false;
            try
            {
                if (!_sendBudget.TryReserve(body.Length))
                {
                    throw new IOException("发送正文预算已满。");
                }
                reserved = true;
                var header = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
                writeStarted = true;
                // Stream.WriteAsync 成功时写完整个 Memory；Socket.SendAsync 的短写循环属于 Socket 适配。
                await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (writeStarted)
                {
                    Interlocked.Exchange(ref _failed, 1);
                }
                throw;
            }
            finally
            {
                if (reserved)
                {
                    _sendBudget.Release(body.Length);
                }
                Volatile.Write(ref _writing, 0);
            }
        }

        private void Enter(ref int direction)
        {
            if (Volatile.Read(ref _failed) != 0)
            {
                throw new IOException("连接分帧已失败，不允许继续复用。");
            }
            if (Interlocked.CompareExchange(ref direction, 1, 0) != 0)
            {
                throw new InvalidOperationException("同方向只允许一个在途 I/O。");
            }
        }
    }
}
