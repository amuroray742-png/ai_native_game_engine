using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Transport;
using Xunit;

namespace AiNative.Server.Tests.Transport
{
    public sealed class FramedStreamTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(100)]
        public async Task FragmentedAndCoalescedFramesKeepTheirBoundaries(int readSize)
        {
            using var stream = new FragmentedStream(new byte[] { 0, 0, 0, 2, 10, 20, 0, 0, 0, 1, 30 }, readSize);
            var framed = Create(stream);
            using ReceivedFrame first = Assert.IsType<ReceivedFrame>(await framed.ReadAsync());
            using ReceivedFrame second = Assert.IsType<ReceivedFrame>(await framed.ReadAsync());
            Assert.Equal(new byte[] { 10, 20 }, first.Body.ToArray());
            Assert.Equal(new byte[] { 30 }, second.Body.ToArray());
            Assert.Null(await framed.ReadAsync());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65)]
        [InlineData(int.MaxValue)]
        public async Task InvalidLengthFailsBeforeReadingBodyAndPoisonsConnection(int length)
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, length);
            using var stream = new FragmentedStream(header, 4);
            var framed = Create(stream);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await framed.ReadAsync());
            Assert.Equal(4, stream.Position);
            await Assert.ThrowsAsync<IOException>(async () => await framed.ReadAsync());
            await Assert.ThrowsAsync<IOException>(async () => await framed.WriteAsync(new byte[] { 1 }));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public async Task TruncatedFrameFailsAndReturnsReservation(int length)
        {
            byte[] complete = { 0, 0, 0, 2, 10, 20 };
            var budget = new FrameBudget(1, 64);
            using var stream = new FragmentedStream(complete.AsSpan(0, length).ToArray(), 1);
            var framed = Create(stream, budget);
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await framed.ReadAsync());
            using var otherStream = new MemoryStream(complete);
            using ReceivedFrame recovered = Assert.IsType<ReceivedFrame>(await Create(otherStream, budget).ReadAsync());
            Assert.Equal(2, recovered.Body.Length);
        }

        [Fact]
        public async Task ReceivedBodyHoldsBudgetUntilDisposedExactlyOnce()
        {
            var budget = new FrameBudget(1, 64);
            using var firstStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 42 });
            ReceivedFrame frame = Assert.IsType<ReceivedFrame>(await Create(firstStream, budget).ReadAsync());
            using var secondStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 43 });
            var second = Create(secondStream, budget);
            await Assert.ThrowsAsync<IOException>(async () => await second.ReadAsync());
            frame.Dispose();
            frame.Dispose();
            Assert.Throws<ObjectDisposedException>(() => frame.Body);
            using var thirdStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 44, 0, 0, 0, 1, 45 });
            var third = Create(thirdStream, budget);
            using ReceivedFrame thirdFrame = Assert.IsType<ReceivedFrame>(await third.ReadAsync());
            Assert.Equal(44, thirdFrame.Body.Span[0]);
            await Assert.ThrowsAsync<IOException>(async () => await third.ReadAsync());
        }

        [Fact]
        public async Task CanceledBodyReadReturnsBudgetAndCannotResume()
        {
            var budget = new FrameBudget(1, 64);
            using var stream = new BlockingStream(new byte[] { 0, 0, 0, 1 });
            var framed = Create(stream, budget);
            using var cancellation = new CancellationTokenSource();
            Task<ReceivedFrame?> read = framed.ReadAsync(cancellation.Token).AsTask();
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await framed.ReadAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            await Assert.ThrowsAsync<IOException>(async () => await framed.ReadAsync());
            using var nextStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 55 });
            using ReceivedFrame next = Assert.IsType<ReceivedFrame>(await Create(nextStream, budget).ReadAsync());
        }

        [Fact]
        public async Task WriteFailureReleasesBudgetAndNeverReportsSuccess()
        {
            var budget = new FrameBudget(1, 64);
            using var stream = new FailingWriteStream();
            var framed = new FramedStream(stream, 64, new FrameBudget(1, 64), budget);
            await Assert.ThrowsAsync<IOException>(async () => await framed.WriteAsync(new byte[] { 10, 20 }));
            Assert.Equal(new byte[] { 0, 0, 0, 2 }, stream.ToArray());
            await Assert.ThrowsAsync<IOException>(async () => await framed.WriteAsync(new byte[] { 30 }));
            using var nextStream = new MemoryStream();
            var next = new FramedStream(nextStream, 64, new FrameBudget(1, 64), budget);
            await next.WriteAsync(new byte[] { 30 });
            Assert.Equal(new byte[] { 0, 0, 0, 1, 30 }, nextStream.ToArray());
        }

        [Fact]
        public async Task PendingWriteHoldsBudgetAndCancellationReleasesIt()
        {
            var budget = new FrameBudget(1, 64);
            using var stream = new BlockingWriteStream();
            var framed = new FramedStream(stream, 64, new FrameBudget(1, 64), budget);
            using var cancellation = new CancellationTokenSource();
            Task write = framed.WriteAsync(new byte[] { 1, 2 }, cancellation.Token).AsTask();
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await framed.WriteAsync(new byte[] { 3 }));
            using var otherStream = new MemoryStream();
            var other = new FramedStream(otherStream, 64, new FrameBudget(1, 64), budget);
            await Assert.ThrowsAsync<IOException>(async () => await other.WriteAsync(new byte[] { 3 }));
            Assert.Empty(otherStream.ToArray());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            await other.WriteAsync(new byte[] { 3 });
            Assert.Equal(new byte[] { 0, 0, 0, 1, 3 }, otherStream.ToArray());
        }

        [Fact]
        public async Task RealTcpSupportsSimultaneousReadAndWriteWithIndependentBudgets()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient { NoDelay = true };
            Task connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            server.NoDelay = true;
            await connect;
            var clientFrames = Create(client.GetStream());
            var serverFrames = Create(server.GetStream());
            Task<ReceivedFrame?> clientRead = clientFrames.ReadAsync(deadline.Token).AsTask();
            Task<ReceivedFrame?> serverRead = serverFrames.ReadAsync(deadline.Token).AsTask();
            await Task.WhenAll(clientFrames.WriteAsync(new byte[] { 1, 2 }, deadline.Token).AsTask(),
                serverFrames.WriteAsync(new byte[] { 3, 4 }, deadline.Token).AsTask());
            using ReceivedFrame receivedByClient = Assert.IsType<ReceivedFrame>(await clientRead);
            using ReceivedFrame receivedByServer = Assert.IsType<ReceivedFrame>(await serverRead);
            Assert.Equal(new byte[] { 3, 4 }, receivedByClient.Body.ToArray());
            Assert.Equal(new byte[] { 1, 2 }, receivedByServer.Body.ToArray());
        }

        private static FramedStream Create(Stream stream, FrameBudget? receiveBudget = null) =>
            new FramedStream(stream, 64, receiveBudget ?? new FrameBudget(4, 256), new FrameBudget(4, 256));

        private sealed class FragmentedStream : MemoryStream
        {
            private readonly int _readSize;

            public FragmentedStream(byte[] bytes, int readSize) : base(bytes)
            {
                _readSize = readSize;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                base.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, _readSize)), cancellationToken);
        }

        private sealed class BlockingStream : MemoryStream
        {
            public readonly TaskCompletionSource Blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public BlockingStream(byte[] prefix) : base(prefix) { }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position < Length)
                {
                    return await base.ReadAsync(buffer, cancellationToken);
                }
                Blocked.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }

        private sealed class FailingWriteStream : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Length != 0)
                {
                    throw new IOException("injected body write failure");
                }
                return base.WriteAsync(buffer, cancellationToken);
            }
        }

        private sealed class BlockingWriteStream : MemoryStream
        {
            public readonly TaskCompletionSource Blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Blocked.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
