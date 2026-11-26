using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Actors;
using Xunit;

namespace AiNative.Server.Tests.Actors
{
    public sealed class ActorTests
    {
        [Fact]
        public async Task DedicatedActorCreatesProcessesAndStopsOnOneThread()
        {
            int caller = Environment.CurrentManagedThreadId;
            int factoryThread = 0;
            var behavior = new Probe();
            var actor = new DedicatedThreadActor<int>("room", 16, 64, () =>
            {
                factoryThread = Environment.CurrentManagedThreadId;
                return behavior;
            });
            await actor.Started.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(PostResult.Accepted, actor.Reference.TryPost(i, 4));
            }
            await actor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(caller, factoryThread);
            Assert.All(behavior.Threads, thread => Assert.Equal(factoryThread, thread));
            Assert.Equal(16, behavior.Messages.Count);
            Assert.Equal(1, behavior.Stops);
            Assert.Equal(PostResult.Stopped, actor.Reference.TryPost(17, 4));
            await actor.StopAsync();
            Assert.Equal(1, behavior.Stops);
        }

        [Theory]
        [InlineData(1, 100)]
        [InlineData(10, 4)]
        public async Task BudgetIncludesExecutingMessageAndStopRejectsNewPosts(int count, long bytes)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var behavior = new Probe { Handle = _ => { entered.Set(); release.Wait(); } };
            var actor = new DedicatedThreadActor<int>("blocked", count, bytes, () => behavior);
            try
            {
                Assert.Equal(PostResult.Accepted, actor.Reference.TryPost(1, 4));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(PostResult.Full, actor.Reference.TryPost(2, 4));
                actor.RequestStop();
                Assert.Equal(PostResult.Stopped, actor.Reference.TryPost(3, 0));
            }
            finally
            {
                release.Set();
                await actor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(new[] { 1 }, behavior.Messages);
        }

        [Fact]
        public async Task FailureDiscardsPendingMessagesAndReportsCleanupFailure()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var behavior = new Probe
            {
                Handle = _ => { entered.Set(); release.Wait(); throw new InvalidOperationException("simulation"); },
                Stop = () => throw new ArgumentException("cleanup")
            };
            var actor = new DedicatedThreadActor<int>("fault", 2, 8, () => behavior);
            try
            {
                actor.Reference.TryPost(1, 4);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(PostResult.Accepted, actor.Reference.TryPost(2, 4));
            }
            finally
            {
                release.Set();
            }
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(async () =>
                await actor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Equal(new[] { 1 }, behavior.Messages);
            Assert.Equal(1, behavior.Stops);
            Assert.Equal(PostResult.Stopped, actor.Reference.TryPost(3, 4));
        }

        [Fact]
        public async Task StartFailureStillCleansUpOnActorThread()
        {
            var behavior = new Probe { Start = () => throw new InvalidOperationException("start") };
            var actor = new DedicatedThreadActor<int>("start-fault", 1, 4, () => behavior);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.Started);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.StopAsync());
            Assert.Equal(1, behavior.Stops);
            Assert.Equal(behavior.Threads[0], behavior.Threads[1]);
        }

        [Fact]
        public async Task SharedActorIsSerialAcrossConcurrentProducersAndMultipleTurns()
        {
            int active = 0;
            int overlap = 0;
            var behavior = new Probe
            {
                Handle = _ =>
                {
                    if (Interlocked.Increment(ref active) != 1)
                    {
                        Interlocked.Increment(ref overlap);
                    }
                    Thread.SpinWait(100);
                    Interlocked.Decrement(ref active);
                }
            };
            var actor = new SharedActor<int>("shared", 1000, 4000, () => behavior, messagesPerTurn: 3);
            await actor.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(Task.Run(() => PostRange(actor.Reference, 0)), Task.Run(() => PostRange(actor.Reference, 500)));
            await actor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, overlap);
            Assert.Equal(1000, behavior.Messages.Count);
            Assert.Equal(1, behavior.Stops);
            int previousLow = -1;
            int previousHigh = 499;
            foreach (int message in behavior.Messages)
            {
                if (message < 500)
                {
                    Assert.Equal(++previousLow, message);
                }
                else
                {
                    Assert.Equal(++previousHigh, message);
                }
            }
        }

        [Fact]
        public async Task ConcurrentStopsNeverRestartSharedCleanup()
        {
            for (int round = 0; round < 100; round++)
            {
                var behavior = new Probe();
                var actor = new SharedActor<int>("stop-race", 1, 4, () => behavior, messagesPerTurn: 1);
                await actor.Started;
                await Task.WhenAll(Task.Run(() => RepeatStop(actor)), Task.Run(() => RepeatStop(actor)), actor.StopAsync());
                Assert.Equal(1, behavior.Stops);
                Assert.Equal(PostResult.Stopped, actor.Reference.TryPost(1, 4));
            }
        }

        [Fact]
        public async Task CancelingStopWaitDoesNotDisposeLiveBehavior()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var behavior = new Probe { Handle = _ => { entered.Set(); release.Wait(); } };
            var actor = new DedicatedThreadActor<int>("stop-cancel", 1, 4, () => behavior);
            try
            {
                actor.Reference.TryPost(1, 4);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actor.StopAsync(cancellation.Token));
                Assert.False(actor.Completion.IsCompleted);
                Assert.Equal(0, behavior.Stops);
            }
            finally
            {
                release.Set();
                await actor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(1, behavior.Stops);
        }

        private static void RepeatStop(SharedActor<int> actor)
        {
            for (int i = 0; i < 100; i++)
            {
                actor.RequestStop();
            }
        }

        [Fact]
        public async Task SharedFailureStopsWithoutRestart()
        {
            var behavior = new Probe { Handle = _ => throw new InvalidOperationException("failed") };
            var actor = new SharedActor<int>("shared-fault", 2, 8, () => behavior);
            await actor.Started;
            actor.Reference.TryPost(1, 4);
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.StopAsync());
            Assert.Equal(1, behavior.Stops);
            Assert.Equal(PostResult.Stopped, actor.Reference.TryPost(2, 4));
        }

        private static void PostRange(ActorRef<int> actor, int start)
        {
            for (int i = start; i < start + 500; i++)
            {
                Assert.Equal(PostResult.Accepted, actor.TryPost(i, 4));
            }
        }

        private sealed class Probe : IActorBehavior<int>
        {
            public readonly List<int> Messages = new List<int>();
            public readonly List<int> Threads = new List<int>();
            public Action Start = () => { };
            public Action<int> Handle = _ => { };
            public Action Stop = () => { };
            public int Stops;

            public void OnStart()
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                Start();
            }

            public void OnMessage(int message)
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                Messages.Add(message);
                Handle(message);
            }

            public void OnStop()
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                Stops++;
                Stop();
            }
        }
    }
}
