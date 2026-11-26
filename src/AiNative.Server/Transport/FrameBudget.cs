using System;

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

        internal void Release(int bytes)
        {
            lock (_gate)
            {
                _frames--;
                _bytes -= bytes;
            }
        }
    }
}
