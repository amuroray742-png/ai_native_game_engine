using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Server.Transport
{
    /// <summary>正文条数与字节硬预算；配额须持有到 I/O 完成或接收正文消费者释放。</summary>
    public sealed class FrameBudget
    {
        private readonly object _gate = new object();
        private readonly int _maxFrames;
        private readonly long _maxBytes;
        private int _frames;
        private long _bytes;
        private TaskCompletionSource? _capacityChanged;

        public FrameBudget(int maxFrames, long maxBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            _maxFrames = maxFrames;
            _maxBytes = maxBytes;
        }

        public long MaxBytes => _maxBytes;

        internal bool TryReserve(int bytes)
        {
            lock (_gate)
            {
                if (_frames == _maxFrames || bytes > _maxBytes - _bytes)
                {
                    return false;
                }
                _frames++;
                _bytes += bytes;
                return true;
            }
        }

        // 在读帧头之前预留条数；长度未知时不占正文额度。
        internal async ValueTask ReserveFrameAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_frames < _maxFrames)
                    {
                        _frames++;
                        return;
                    }
                    _capacityChanged ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed = _capacityChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // 调用者已持有一个条数额度，且长度已通过最大正文校验。
        internal async ValueTask ReserveBodyAsync(int bytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytes <= _maxBytes - _bytes)
                    {
                        _bytes += bytes;
                        return;
                    }
                    _capacityChanged ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed = _capacityChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        internal void Release(int bytes)
        {
            TaskCompletionSource? changed;
            lock (_gate)
            {
                _frames--;
                _bytes -= bytes;
                changed = _capacityChanged;
                _capacityChanged = null;
            }
            changed?.TrySetResult();
        }
    }
}
