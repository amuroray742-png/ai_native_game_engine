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
        public async Task FrameLimitPausesBeforeHeaderAndDoubleDisposeDoesNotIncreaseCapacity()
        {
            var budget = new FrameBudget(1, 64);
            using var stream = new MemoryStream(new byte[] { 0, 0, 0, 1, 42, 0, 0, 0, 1, 43, 0, 0, 0, 1, 44 });
            var framed = Create(stream, budget);
            ReceivedFrame first = Assert.IsType<ReceivedFrame>(await framed.ReadAsync());
            Task<ReceivedFrame?> secondRead = framed.ReadAsync().AsTask();
            Assert.False(secondRead.IsCompleted);
            Assert.Equal(5, stream.Position);
            first.Dispose();
            first.Dispose();
            Assert.Throws<ObjectDisposedException>(() => first.Body);
            ReceivedFrame second = Assert.IsType<ReceivedFrame>(await secondRead.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(43, second.Body.Span[0]);
            Task<ReceivedFrame?> thirdRead = framed.ReadAsync().AsTask();
            Assert.False(thirdRead.IsCompleted);
            Assert.Equal(10, stream.Position);
            second.Dispose();
            using ReceivedFrame third = Assert.IsType<ReceivedFrame>(await thirdRead.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(44, third.Body.Span[0]);
        }

        [Fact]
        public async Task ByteLimitPausesAfterHeaderUntilConsumerReleasesBody()
        {
            var budget = new FrameBudget(2, 3);
            using var stream = new MemoryStream(new byte[] { 0, 0, 0, 2, 10, 20, 0, 0, 0, 2, 30, 40 });
            var framed = new FramedStream(stream, 3, budget, new FrameBudget(1, 3));
            ReceivedFrame first = Assert.IsType<ReceivedFrame>(await framed.ReadAsync());
            Task<ReceivedFrame?> pending = framed.ReadAsync().AsTask();
            Assert.False(pending.IsCompleted);
            Assert.Equal(10, stream.Position);
            first.Dispose();
            using ReceivedFrame second = Assert.IsType<ReceivedFrame>(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new byte[] { 30, 40 }, second.Body.ToArray());
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 4)]
        public async Task CancelingCapacityWaitReleasesOnlyItsOwnReservation(int maxFrames, int readBytes)
        {
            var budget = new FrameBudget(maxFrames, 1);
            using var heldStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 10 });
            var heldReader = new FramedStream(heldStream, 1, budget, new FrameBudget(1, 1));
            ReceivedFrame held = Assert.IsType<ReceivedFrame>(await heldReader.ReadAsync());
            using var stream = new MemoryStream(new byte[] { 0, 0, 0, 1, 20 });
            var reader = new FramedStream(stream, 1, budget, new FrameBudget(1, 1));
            using var cancellation = new CancellationTokenSource();
            Task<ReceivedFrame?> pending = reader.ReadAsync(cancellation.Token).AsTask();
            Assert.False(pending.IsCompleted);
            Assert.Equal(readBytes, stream.Position);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAsync<IOException>(async () => await reader.ReadAsync());
            using var nextStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 30 });
            var next = new FramedStream(nextStream, 1, budget, new FrameBudget(1, 1));
            Task<ReceivedFrame?> nextRead = next.ReadAsync().AsTask();
            Assert.False(nextRead.IsCompleted);
            held.Dispose();
            using ReceivedFrame recovered = Assert.IsType<ReceivedFrame>(await nextRead.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(30, recovered.Body.Span[0]);
        }

        [Fact]
        public async Task BoundaryEofReturnsReservedFrameSlot()
        {
            var budget = new FrameBudget(1, 64);
            using var empty = new MemoryStream();
            Assert.Null(await Create(empty, budget).ReadAsync());
            using var next = new MemoryStream(new byte[] { 0, 0, 0, 1, 10 });
            using ReceivedFrame received = Assert.IsType<ReceivedFrame>(
                await Create(next, budget).ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
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

        [Fact]
        public async Task TcpReceiveBackpressureDoesNotBlockRepliesAndResumesInOrder()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient { NoDelay = true };
            Task connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            await connect;
            var clientFrames = Create(client.GetStream());
            var serverFrames = Create(server.GetStream(), new FrameBudget(1, 64));
            await clientFrames.WriteAsync(new byte[] { 1 }, deadline.Token);
            ReceivedFrame first = Assert.IsType<ReceivedFrame>(await serverFrames.ReadAsync(deadline.Token));
            await clientFrames.WriteAsync(new byte[] { 2 }, deadline.Token);
            Task<ReceivedFrame?> pending = serverFrames.ReadAsync(deadline.Token).AsTask();
            Assert.False(pending.IsCompleted);
            await serverFrames.WriteAsync(new byte[] { 3 }, deadline.Token);
            using ReceivedFrame reply = Assert.IsType<ReceivedFrame>(await clientFrames.ReadAsync(deadline.Token));
            Assert.Equal(3, reply.Body.Span[0]);
            first.Dispose();
            using ReceivedFrame second = Assert.IsType<ReceivedFrame>(await pending);
            Assert.Equal(2, second.Body.Span[0]);
        }

        [Fact]
        public async Task SharedBudgetWakesWaitersWithoutExceedingFrameLimit()
        {
            var budget = new FrameBudget(1, 64);
            using var heldStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 10 });
            ReceivedFrame held = Assert.IsType<ReceivedFrame>(await Create(heldStream, budget).ReadAsync());
            using var firstStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 20 });
            using var secondStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 30 });
            Task<ReceivedFrame?> first = Create(firstStream, budget).ReadAsync().AsTask();
            Task<ReceivedFrame?> second = Create(secondStream, budget).ReadAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            held.Dispose();
            Task<ReceivedFrame?> winner = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Task<ReceivedFrame?> loser = winner == first ? second : first;
            ReceivedFrame received = Assert.IsType<ReceivedFrame>(await winner);
            Assert.False(loser.IsCompleted);
            received.Dispose();
            using ReceivedFrame remaining = Assert.IsType<ReceivedFrame>(await loser.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(received, remaining);
        }

        [Fact]
        public async Task ConcurrentCancellationAndReleaseLeaveBudgetReusable()
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                var budget = new FrameBudget(1, 64);
                using var heldStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 10 });
                ReceivedFrame held = Assert.IsType<ReceivedFrame>(await Create(heldStream, budget).ReadAsync());
                using var pendingStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 20 });
                using var cancellation = new CancellationTokenSource();
                Task<ReceivedFrame?> pending = Create(pendingStream, budget).ReadAsync(cancellation.Token).AsTask();
                await Task.WhenAll(Task.Run(held.Dispose), Task.Run(cancellation.Cancel));
                try
                {
                    using ReceivedFrame received = Assert.IsType<ReceivedFrame>(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(20, received.Body.Span[0]);
                }
                catch (OperationCanceledException)
                {
                    // 取消与成功读取竞争，两种结果都必须完整归还配额。
                }
                using var nextStream = new MemoryStream(new byte[] { 0, 0, 0, 1, 30 });
                using ReceivedFrame next = Assert.IsType<ReceivedFrame>(
                    await Create(nextStream, budget).ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public async Task PartialProgressDoesNotRefreshWholeFrameDeadline()
        {
            using var stream = new DrippingStream();
            var framed = Create(stream);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => framed.ReadAsync(
                firstByteTimeout: TimeSpan.FromSeconds(1), frameTimeout: TimeSpan.FromMilliseconds(70)).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<IOException>(async () => await framed.ReadAsync());
        }

        private sealed class DrippingStream : MemoryStream
        {
            private int _position;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(30, cancellationToken);
                buffer.Span[0] = _position++ == 3 ? (byte)64 : (byte)0;
                return 1;
            }
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
