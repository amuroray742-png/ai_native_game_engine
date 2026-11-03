using System;
using AiNative.Core;
using AiNative.Hosting;
using Xunit;

namespace AiNative.Tests
{
    public sealed class PresentationTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SequentialPlayback_PreservesHistoricalStatesAndDoesNotAdvanceLogic(bool immediate)
        {
            using var game = CoreTests.Create();
            var queue = new SequentialPresentationQueue<string[]>(game.Capture());
            for (int index = 0; index < 3; index++)
            {
                game.Submit(index.ToString());
                queue.Enqueue(game.Advance());
                if (immediate)
                {
                    TickResult<string[]> frame = queue.BeginNext()!;
                    Assert.Equal(index + 1, frame.Observation.State.Length);
                    Assert.True(queue.Complete(frame));
                }
            }
            if (!immediate)
            {
                for (int index = 0; index < 3; index++)
                {
                    TickResult<string[]> frame = queue.BeginNext()!;
                    Assert.Equal(index + 1, frame.Observation.State.Length);
                    Assert.Throws<InvalidOperationException>(() => queue.BeginNext());
                    Assert.True(queue.Complete(frame));
                    Assert.False(queue.Complete(frame));
                }
            }
            Assert.True(queue.IsSettledAt(game.Capture()));
            Assert.Equal(3, game.Tick);
            game.Submit("pending");
            Assert.False(queue.IsSettledAt(game.Capture()));
        }

        [Fact]
        public void Reset_InvalidatesOldAnimationBeforeItWritesAndRejectsForeignFrames()
        {
            using var old = CoreTests.Create();
            using var fresh = CoreTests.Create();
            var queue = new SequentialPresentationQueue<string[]>(old.Capture());
            old.Submit("old");
            TickResult<string[]> frame = old.Advance();
            queue.Enqueue(frame);
            TickResult<string[]> active = queue.BeginNext()!;
            queue.Reset(fresh.Capture());
            bool wrote = false;
            if (queue.IsActive(active))
            {
                wrote = true;
                queue.Complete(active);
            }
            Assert.False(wrote);
            Assert.False(queue.Complete(active));
            Assert.Throws<InvalidOperationException>(() => queue.Enqueue(frame));
            TickResult<string[]> first = fresh.Advance(), second = fresh.Advance();
            Assert.Throws<InvalidOperationException>(() => queue.Enqueue(second));
            queue.Enqueue(first);
            Assert.Throws<InvalidOperationException>(() => queue.Enqueue(first));
        }

        [Fact]
        public void RealtimeBuffer_KeepsTwoFramesWithoutDroppingEventsOrRejectedInputs()
        {
            using var game = CoreTests.Create();
            var buffer = new RealtimePresentationBuffer<string[]>(game.Capture());
            for (int index = 0; index < 10; index++)
            {
                game.Submit(index == 5 ? "reject" : "event");
                buffer.Push(game.Advance());
            }
            Assert.Equal(9, buffer.Previous.Tick);
            Assert.Equal(10, buffer.Current.Tick);
            Assert.Equal(10, buffer.PendingEventBatches);
            Assert.False(buffer.IsAt(game.Capture()));
            for (int index = 0; index < 10; index++)
            {
                PresentationEvents batch = buffer.TakeEvents()!;
                Assert.Equal(index + 1, batch.Tick);
                Assert.True(buffer.IsActive(batch));
                Assert.Equal(index != 5, Assert.Single(batch.Inputs).Result.Succeeded);
                Assert.Equal(index == 5 ? 0 : 1, batch.Events.Count);
            }
            Assert.Null(buffer.TakeEvents());
            Assert.True(buffer.IsAt(game.Capture()));
            game.Submit("new");
            buffer.Push(game.Advance());
            PresentationEvents old = buffer.TakeEvents()!;
            buffer.Reset(game.Capture());
            Assert.False(buffer.IsActive(old));
        }
    }
}
