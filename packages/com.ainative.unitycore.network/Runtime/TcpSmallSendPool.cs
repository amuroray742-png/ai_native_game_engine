#nullable enable
using System.Buffers;
using System.Threading;

namespace AiNative.UnityCore.Network
{
    // The main thread rents; only the socket writer returns to this ring.
    internal sealed class TcpSmallSendPool : ArrayPool<byte>
    {
        internal const int FrameSize = 256;
        private readonly byte[]?[] _buffers = new byte[128][];
        private readonly ArrayPool<byte> _owner;
        private int _head;
        private int _tail;

        internal TcpSmallSendPool(ArrayPool<byte> owner) { _owner = owner; }

        public override byte[] Rent(int minimumLength)
        {
            if (_head == Volatile.Read(ref _tail) || (uint)minimumLength > FrameSize) { return _owner.Rent(minimumLength); }
            int slot = _head & (_buffers.Length - 1);
            var buffer = _buffers[slot]!;
            _buffers[slot] = null;
            Volatile.Write(ref _head, unchecked(_head + 1));
            return buffer;
        }

        public override void Return(byte[] buffer, bool clearArray = false)
        {
            if (buffer.Length != FrameSize || unchecked(_tail - Volatile.Read(ref _head)) == _buffers.Length)
            {
                _owner.Return(buffer, clearArray);
                return;
            }
            if (clearArray) { System.Array.Clear(buffer, 0, buffer.Length); }
            _buffers[_tail & (_buffers.Length - 1)] = buffer;
            Volatile.Write(ref _tail, unchecked(_tail + 1));
        }

        // Called after worker retirement, with no producer or consumer left.
        internal void Drain()
        {
            while (_head != _tail) { _owner.Return(Rent(FrameSize)); }
        }
    }
}
