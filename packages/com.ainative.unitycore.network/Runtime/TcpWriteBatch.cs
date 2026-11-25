using System;
using System.IO;

namespace AiNative.UnityCore.Network
{
    // Owned by the sender. Offsets refer to the encoded stream, including frame headers.
    internal sealed class TcpWriteBatch
    {
        internal const int MaxFrames = 32;
        private readonly byte[] _bytes = new byte[MaxFrames * TcpSmallSendPool.FrameSize];
        private readonly int[] _ends = new int[MaxFrames];
        internal int Count { get; private set; }
        private int _length;

        internal void Reset() { Count = 0; _length = 0; }

        internal void Append(ArraySegment<byte> frame)
        {
            Buffer.BlockCopy(frame.Array, frame.Offset, _bytes, _length, frame.Count);
            _length += frame.Count;
            _ends[Count++] = _length;
        }

        internal void Write(Func<byte[], int, int, int> send, Action<int, int> publish)
        {
            int offset = 0, complete = 0, publishedBytes = 0;
            while (offset < _length)
            {
                int accepted = send(_bytes, offset, _length - offset);
                if (accepted == 0) { throw new IOException("TCP send made no progress."); }
                offset += accepted;
                int previous = complete;
                while (complete < Count && _ends[complete] <= offset) { complete++; }
                if (complete > previous)
                {
                    int end = _ends[complete - 1];
                    publish(complete - previous, end - publishedBytes);
                    publishedBytes = end;
                }
            }
        }
    }
}
