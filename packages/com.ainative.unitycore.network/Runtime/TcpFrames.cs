using System;
using System.IO;
using System.Net.Sockets;
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

        internal static void Write(byte[] frame, Func<byte[], int, int, int> send)
        {
            int offset = 0;
            while (offset < frame.Length)
            {
                int count = send(frame, offset, frame.Length - offset);
                if (count == 0) { throw new IOException("TCP send made no progress."); }
                offset += count;
            }
        }

        // null denotes EOF exactly between frames. Truncation inside a frame is a protocol failure.
        internal static byte[] Read(Socket socket, int maximum, byte[] header)
        {
            int count = socket.Receive(header, SocketFlags.None);
            if (count == 0) { return null; }
            ReadRemaining(socket, header, count);
            uint length = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (length > maximum) { throw new InvalidDataException("TCP message exceeds the configured maximum."); }
            var payload = new byte[(int)length];
            ReadRemaining(socket, payload, 0);
            return payload;
        }

        private static void ReadRemaining(Socket socket, byte[] buffer, int offset)
        {
            while (offset < buffer.Length)
            {
                int count = socket.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
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
