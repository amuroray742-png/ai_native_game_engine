using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Transport;
using Xunit;

namespace AiNative.Server.Tests.Transport
{
    public sealed class BoundedSendQueueTests
    {
        [Fact]
        public async Task ControlReserveAndByteRoundsPreserveRouteFifo()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits(20, 100, 4, 20, 8, 40, 4, 2));
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            var completions = new List<Task>();
            foreach (byte value in new byte[] { 1, 2, 3, 4 })
            { Enqueue(queue, first, new byte[] { value, 0 }, completions); }
            Enqueue(queue, second, new byte[] { 5, 0, 0, 0 }, completions);
            Enqueue(queue, second, new byte[] { 6, 0, 0, 0 }, completions);
            for (byte value = 10; value < 14; value++)
            {
                Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueControl(new byte[] { value }, out Task? written));
                completions.Add(written!);
            }
            queue.Complete();
            using var stream = new MemoryStream();
            await queue.RunAsync(Frames(stream));
            await Task.WhenAll(completions);
            Assert.Equal(new byte[] { 10, 11, 1, 12, 13, 2, 5, 3, 4, 6 }, await Markers(stream));
            Assert.Equal(SendAdmission.Stopped, queue.TryEnqueueRoute(first, new byte[] { 8 }, out _));
        }

        [Fact]
        public async Task InFlightBodyKeepsRouteAndTotalBudgetUntilActualWriteEnds()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits(3, 12, 1, 4, 1, 4, 4, 2));
            Guid route = Guid.NewGuid();
            byte[] body = { 1, 2, 3, 4 };
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(route, body, out Task? first));
            body[0] = 99;
            using var stream = new GatedStream();
            Task runner = queue.RunAsync(Frames(stream));
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first!.IsCompleted);
            Assert.Equal(SendAdmission.Full, queue.TryEnqueueRoute(route, new byte[] { 2 }, out _));
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(Guid.NewGuid(), new byte[] { 5, 6, 7, 8 }, out Task? other));
            Assert.Equal(SendAdmission.Full, queue.TryEnqueueRoute(Guid.NewGuid(), new byte[] { 3 }, out _));
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueControl(new byte[] { 9 }, out Task? control));
            queue.Complete();
            stream.Release.TrySetResult();
            await runner.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(first, other!, control!);
            Assert.Equal(new byte[] { 1, 9, 5 }, await Markers(stream));
        }

        [Fact]
        public async Task WriteFailureFailsCurrentAndPendingInsteadOfReportingSuccess()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits());
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(Guid.NewGuid(), new byte[] { 1 }, out Task? first));
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(Guid.NewGuid(), new byte[] { 2 }, out Task? second));
            using var stream = new BrokenStream();
            await Assert.ThrowsAsync<IOException>(() => queue.RunAsync(Frames(stream)));
            await Assert.ThrowsAsync<IOException>(() => first!);
            await Assert.ThrowsAsync<IOException>(() => second!);
            Assert.Equal(SendAdmission.Stopped, queue.TryEnqueueControl(new byte[] { 3 }, out _));
        }

        [Fact]
        public async Task CancelingBlockedWriterSettlesEveryAdmittedFrame()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits());
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueControl(new byte[] { 1 }, out Task? first));
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(Guid.NewGuid(), new byte[] { 2 }, out Task? second));
            using var stream = new GatedStream();
            using var cancel = new CancellationTokenSource();
            Task runner = queue.RunAsync(Frames(stream), cancel.Token);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first!);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second!);
        }

        [Fact]
        public async Task EmptyWriterWakesForNewFrameAndForCompletion()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits());
            using var stream = new MemoryStream();
            Task runner = queue.RunAsync(Frames(stream));
            Assert.False(runner.IsCompleted);
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueControl(new byte[] { 1 }, out Task? written));
            await written!.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Complete();
            await runner.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(Frames(stream)));
        }

        [Fact]
        public async Task SameRouteCanQueueAgainWhileItsLastFrameIsWriting()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits());
            Guid route = Guid.NewGuid();
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(route, new byte[] { 1 }, out Task? first));
            using var stream = new GatedStream();
            Task runner = queue.RunAsync(Frames(stream));
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(route, new byte[] { 2 }, out Task? second));
            queue.Complete();
            stream.Release.TrySetResult();
            await runner.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(first!, second!);
            Assert.Equal(new byte[] { 1, 2 }, await Markers(stream));
        }

        [Fact]
        public async Task LargeFrameAccumulatesQuantumWithoutStarvingSmallRoute()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits(20, 100, 4, 20, 8, 40, 4, 2));
            Guid large = Guid.NewGuid();
            Guid small = Guid.NewGuid();
            var writes = new List<Task>();
            Enqueue(queue, large, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, writes);
            Enqueue(queue, small, new byte[] { 2, 0, 0, 0 }, writes);
            Enqueue(queue, small, new byte[] { 3, 0, 0, 0 }, writes);
            queue.Complete();
            using var stream = new MemoryStream();
            await queue.RunAsync(Frames(stream));
            await Task.WhenAll(writes);
            Assert.Equal(new byte[] { 2, 3, 1 }, await Markers(stream));
        }

        [Fact]
        public async Task ConcurrentCompletionSettlesExactlyTheAcceptedPrefix()
        {
            for (int iteration = 0; iteration < 50; iteration++)
            {
                var queue = new BoundedSendQueue(new SendQueueLimits());
                using var stream = new MemoryStream();
                Task runner = queue.RunAsync(Frames(stream));
                var accepted = new List<Task>();
                Task producer = Task.Run(() =>
                {
                    for (int item = 0; item < 50; item++)
                    {
                        SendAdmission result = queue.TryEnqueueRoute(Guid.Empty, new byte[] { (byte)item }, out Task? written);
                        if (result == SendAdmission.Accepted)
                        { accepted.Add(written!); }
                        else
                        { Assert.Equal(SendAdmission.Stopped, result); break; }
                    }
                });
                await Task.WhenAll(producer, Task.Run(queue.Complete));
                await runner.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(accepted);
                byte[] markers = await Markers(stream);
                Assert.Equal(accepted.Count, markers.Length);
                for (int item = 0; item < markers.Length; item++)
                { Assert.Equal((byte)item, markers[item]); }
            }
        }

        [Fact]
        public async Task WriteDeadlineFailsStalledFrameAndPendingWork()
        {
            var queue = new BoundedSendQueue(new SendQueueLimits());
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueControl(new byte[] { 1 }, out Task? first));
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(Guid.NewGuid(), new byte[] { 2 }, out Task? pending));
            using var stream = new GatedStream();
            Task run = queue.RunAsync(Frames(stream), writeTimeout: TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first!);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending!);
        }

        private static void Enqueue(BoundedSendQueue queue, Guid route, byte[] body, List<Task> completions)
        {
            Assert.Equal(SendAdmission.Accepted, queue.TryEnqueueRoute(route, body, out Task? written));
            completions.Add(written!);
        }

        private static FramedStream Frames(Stream stream) => new FramedStream(stream, 64, new FrameBudget(32, 2048), new FrameBudget(1, 64));

        private static async Task<byte[]> Markers(MemoryStream stream)
        {
            stream.Position = 0;
            var result = new List<byte>();
            var frames = Frames(stream);
            while (true)
            {
                using ReceivedFrame? frame = await frames.ReadAsync();
                if (frame == null)
                { return result.ToArray(); }
                result.Add(frame.Body.Span[0]);
            }
        }

        private sealed class GatedStream : MemoryStream
        {
            internal readonly TaskCompletionSource Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                await base.WriteAsync(buffer, cancellationToken);
            }
        }

        private sealed class BrokenStream : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromException(new IOException("injected write failure"));
        }
    }
}
