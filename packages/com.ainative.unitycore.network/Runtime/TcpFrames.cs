using System;
using System.IO;
using System.Threading.Tasks;

namespace AiNative.UnityCore.Network
{
    internal static class TcpFrames
    {
        internal static byte[] Encode(byte[] payload)
        {
            int length = payload.Length;
            var frame = new byte[checked(4 + length)];
            frame[0] = (byte)(length >> 24);
            frame[1] = (byte)(length >> 16);
            frame[2] = (byte)(length >> 8);
            frame[3] = (byte)length;
            Buffer.BlockCopy(payload, 0, frame, 4, length);
            return frame;
        }

        // null denotes EOF exactly between frames. Truncation inside a frame is a protocol failure.
        internal static async Task<byte[]> ReadAsync(Stream stream, int maximum)
        {
            var header = new byte[4];
            int count = await stream.ReadAsync(header, 0, 4).ConfigureAwait(false);
            if (count == 0) { return null; }
            await ReadRemaining(stream, header, count).ConfigureAwait(false);
            uint length = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (length > maximum) { throw new InvalidDataException("TCP message exceeds the configured maximum."); }
            var payload = new byte[(int)length];
            await ReadRemaining(stream, payload, 0).ConfigureAwait(false);
            return payload;
        }

        private static async Task ReadRemaining(Stream stream, byte[] buffer, int offset)
        {
            while (offset < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);
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
