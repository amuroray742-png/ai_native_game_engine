using System;
using System.Threading;

namespace AiNative.Server.Transport
{
    /// <summary>独占接收正文。消费者完成解码或转交后 Dispose，归还预算；当前不使用池化。</summary>
    public sealed class ReceivedFrame : IDisposable
    {
        private readonly FrameBudget _budget;
        private byte[]? _body;

        internal ReceivedFrame(byte[] body, FrameBudget budget)
        {
            _body = body;
            _budget = budget;
        }

        public ReadOnlyMemory<byte> Body => _body ?? throw new ObjectDisposedException(nameof(ReceivedFrame));

        public void Dispose()
        {
            byte[]? body = Interlocked.Exchange(ref _body, null);
            if (body != null)
            {
                _budget.Release(body.Length);
            }
        }
    }
}
