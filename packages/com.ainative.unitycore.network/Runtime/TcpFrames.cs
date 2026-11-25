#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AiNative.UnityCore.Network
{
    internal static class TcpFrames
    {
        internal const int HeaderSize = 4;

        // Unity's shared pool serves ordinary packets. Retain fewer oversized arrays.
        private static readonly ArrayPool<byte> LargeBuffers = ArrayPool<byte>.Create(16 * 1024 * 1024, 2);

        private static ArrayPool<byte> Pool(int length, ArrayPool<byte>? pool) =>
            pool ?? (length <= 1024 * 1024 ? ArrayPool<byte>.Shared : LargeBuffers);

        internal static void Return(ArraySegment<byte> frame, ArrayPool<byte>? pool)
        {
            if (frame.Count != 0) { Pool(frame.Count, pool).Return(frame.Array!); }
        }

        internal static ArraySegment<byte> Encode(byte[] payload, ArrayPool<byte>? pool)
        {
            int length = payload.Length;
            int encodedLength = checked(HeaderSize + length);
            var frame = Pool(encodedLength, pool).Rent(encodedLength);
            frame[0] = (byte)(length >> 24);
            frame[1] = (byte)(length >> 16);
            frame[2] = (byte)(length >> 8);
            frame[3] = (byte)length;
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, length);
            return new ArraySegment<byte>(frame, 0, encodedLength);
        }

        internal static void Write(ArraySegment<byte> frame, Func<byte[], int, int, int> send)
        {
            int offset = 0;
            while (offset < frame.Count)
            {
                int count = send(frame.Array!, frame.Offset + offset, frame.Count - offset);
                if (count == 0) { throw new IOException("TCP send made no progress."); }
                offset += count;
            }
        }

        // null denotes EOF exactly between frames. Truncation inside a frame is a protocol failure.
        internal static int? ReadLength(Socket socket, int maximum, byte[] header)
        {
            int count = socket.Receive(header, SocketFlags.None);
            if (count == 0) { return null; }
            ReadRemaining(socket, header, count, HeaderSize);
            uint length = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (length > maximum) { throw new InvalidDataException("TCP message exceeds the configured maximum."); }
            return (int)length;
        }

        internal static ArraySegment<byte> ReadPayload(Socket socket, int length, ArrayPool<byte>? pool)
        {
            if (length == 0) { return new ArraySegment<byte>(Array.Empty<byte>()); }
            var owner = Pool(length, pool);
            var payload = owner.Rent(length);
            try
            {
                ReadRemaining(socket, payload, 0, length);
                return new ArraySegment<byte>(payload, 0, length);
            }
            catch
            {
                owner.Return(payload);
                throw;
            }
        }

        private static void ReadRemaining(Socket socket, byte[] buffer, int offset, int length)
        {
            while (offset < length)
            {
                int count = socket.Receive(buffer, offset, length - offset, SocketFlags.None);
                if (count == 0) { throw new InvalidDataException("TCP connection ended inside a frame."); }
                offset += count;
            }
        }

        // After socket disposal an in-flight operation may still fault. Its owner already
        // published the terminal outcome; observe the OS completion even after leaving Play.
        internal static async Task DrainAsync(Task operation)
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception) when (operation.IsFaulted || operation.IsCanceled) { /* Observed retired I/O. */ }
        }
    }
}
