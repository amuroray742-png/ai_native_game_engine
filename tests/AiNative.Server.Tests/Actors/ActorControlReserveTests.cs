using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Actors;
using Xunit;

namespace AiNative.Server.Tests.Actors
{
    public sealed class ActorControlReserveTests
    {
        [Theory]
        [InlineData(true, 3, 100)]
        [InlineData(false, 3, 100)]
        [InlineData(true, 10, 6)]
        [InlineData(false, 10, 6)]
        public async Task ControlReserveSurvivesOrdinarySaturationAndPreservesFifo(bool dedicated, int maxMessages, long maxBytes)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var seen = new List<int>();
            Func<IActorBehavior<int>> factory = () => new Behavior(entered, release, seen);
            ActorRef<int> reference;
            Func<Task> stop;
            if (dedicated)
            {
                var actor = new DedicatedThreadActor<int>("room", maxMessages, maxBytes, factory, 1, 2);
                reference = actor.Reference;
                stop = () => actor.StopAsync();
            }
            else
            {
                var actor = new SharedActor<int>("session", maxMessages, maxBytes, factory, reservedControlMessages: 1, reservedControlBytes: 2);
                reference = actor.Reference;
                stop = () => actor.StopAsync();
            }
            try
            {
                Assert.Equal(PostResult.Accepted, reference.TryPost(1, 2));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(PostResult.Accepted, reference.TryPost(2, 2));
                Assert.Equal(PostResult.Full, reference.TryPost(9, 2));
                Assert.Equal(PostResult.Accepted, reference.TryPostControl(3, 2));
                Assert.Equal(PostResult.Full, reference.TryPostControl(4, 100));
            }
            finally { release.Set(); }
            await stop().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 2, 3 }, seen);
            Assert.Equal(PostResult.Stopped, reference.TryPostControl(4, 1));
        }

        private sealed class Behavior : IActorBehavior<int>
        {
            private readonly ManualResetEventSlim _entered;
            private readonly ManualResetEventSlim _release;
            private readonly List<int> _seen;
            internal Behavior(ManualResetEventSlim entered, ManualResetEventSlim release, List<int> seen)
            {
                _entered = entered;
                _release = release;
                _seen = seen;
            }
            public void OnStart() { }
            public void OnStop() { }
            public void OnMessage(int message)
            {
                _seen.Add(message);
                if (message == 1)
                { _entered.Set(); _release.Wait(); }
            }
        }
    }
}
