using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiNative.Server.Lifecycle;
using Xunit;

namespace AiNative.Server.Tests.Lifecycle
{
    public sealed class RoleLifetimeTests
    {
        [Fact]
        public async Task DrainClearsReadinessAndShutdownRunsOnceInReverseOrder()
        {
            var lifetime = new RoleLifetime();
            var order = new List<string>();
            lifetime.Register("internal", _ => { order.Add("internal"); return Task.CompletedTask; });
            lifetime.Register("admission", _ => { order.Add("admission"); return Task.CompletedTask; });
            lifetime.MarkLive();
            lifetime.MarkReady();
            lifetime.BeginDrain();
            Assert.Equal(RoleState.Draining, lifetime.State);
            Task<RoleStopResult> first = lifetime.StopAsync(TimeSpan.FromSeconds(1));
            Assert.Same(first, lifetime.StopAsync(TimeSpan.FromSeconds(2)));
            Assert.True((await first).Clean);
            Assert.Equal(new[] { "admission", "internal" }, order);
            Assert.Equal(RoleState.Stopped, lifetime.State);
            Assert.Throws<InvalidOperationException>(lifetime.MarkLive);
        }

        [Fact]
        public async Task SharedDeadlineDoesNotPreventRemainingCleanupAttempts()
        {
            var lifetime = new RoleLifetime();
            bool remainingAttempted = false;
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lifetime.Register("remaining", token => { remainingAttempted = true; Assert.True(token.IsCancellationRequested); return Task.CompletedTask; });
            lifetime.Register("stalled", _ => never.Task);
            RoleStopResult result = await lifetime.StopAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Clean);
            Assert.True(remainingAttempted);
            Assert.Equal(new RoleStopFailure("stalled", true), Assert.Single(result.Failures));
            never.SetException(new InvalidOperationException("late failure"));
            Assert.Equal(RoleState.Faulted, lifetime.State);
        }

        [Fact]
        public async Task PartialStartupAndCriticalFailureCannotReportCleanStop()
        {
            var lifetime = new RoleLifetime();
            bool cleaned = false;
            lifetime.Register("first", _ => { cleaned = true; return Task.CompletedTask; });
            lifetime.ReportCriticalFailure();
            RoleStopResult result = await lifetime.StopAsync(TimeSpan.FromSeconds(1));
            Assert.True(cleaned);
            Assert.False(result.Clean);
            Assert.Equal(RoleState.Faulted, lifetime.State);
        }
    }
}
