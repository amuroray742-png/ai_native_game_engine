using System;
using System.Collections.Generic;
using AiNative.Core;
using AiNative.Hosting;
using AiNative.Unity;
using NUnit.Framework;

namespace AiNative.Examples.GridGame.PlayTests
{
    public sealed class ClockTests
    {
        private sealed class Counter : IGameSimulation<string, long, long>, IPlaySession<string, long>
        {
            private readonly SimulationRuntime<string, long, long> _runtime;
            internal readonly List<string> Inputs = new List<string>();
            internal bool FailCapture;
            internal bool FailDisposal;
            internal int Disposals;
            public SimulationTiming Timing { get; }
            internal Counter(int hz)
            {
                Timing = new SimulationTiming(hz);
                _runtime = SimulationRuntime<string, long, long>.Create(() => this, 42, Timing);
            }
            public long Submit(string command) => _runtime.Submit(command);
            public TickResult<long> AdvanceTick() => _runtime.Advance();
            public Observation<long> CaptureObservation() => FailCapture ? throw new InvalidOperationException() : _runtime.Capture();
            public CommandResult Execute(string command, DeterministicRandom random)
            {
                Inputs.Add(command);
                return CommandResult.Accepted();
            }
            public IReadOnlyList<GameEvent> Advance(StepContext context) => Array.Empty<GameEvent>();
            public long GetState() => Inputs.Count;
            public long ExportState() => Inputs.Count;
            void IDisposable.Dispose()
            {
                Disposals++;
                if (FailDisposal)
                {
                    throw new InvalidOperationException("injected cleanup failure");
                }
            }
        }

        [Test]
        public void FixedPumpRetainsDebtDuringPauseAndPublishesOrderedInputsOnce()
        {
            using var host = new PlayHost<string, long>();
            var game = new Counter(60);
            host.Replace(() => game);
            host.Submit("a");
            host.Submit("b");
            var frames = host.Realtime;
            host.Pump(.5 / 60, false);
            Assert.AreEqual(0, frames.Current.Tick);
            host.Pump(.5 / 60, false);
            Assert.AreEqual(1, frames.Current.Tick);
            host.Pump(10d / 60, false);
            Assert.AreEqual(5, frames.Current.Tick);
            host.Pump(30, true);
            Assert.AreEqual(5, frames.Current.Tick);
            host.Pump(0, false);
            Assert.AreEqual(9, frames.Current.Tick);
            host.Pump(0, false);
            Assert.AreEqual(11, frames.Current.Tick);
            Assert.AreEqual(10, frames.Previous.Tick);
            CollectionAssert.AreEqual(new[] { "a", "b" }, game.Inputs);
            var batch = frames.TakeEvents();
            Assert.AreEqual(1, batch.Tick);
            Assert.AreEqual(2, batch.Inputs.Count);
            Assert.IsNull(frames.TakeEvents());
            Assert.Throws<InvalidOperationException>(() => host.AdvanceManual());
            host.Dispose();
            Assert.IsFalse(frames.IsActive(batch));
            Assert.AreEqual(1, game.Disposals);
        }

        [Test]
        public void BrokenOldCleanupReleasesCandidateAndAllowsFreshStart()
        {
            using var host = new PlayHost<string, long>();
            var old = new Counter(0) { FailDisposal = true };
            var candidate = new Counter(0);
            host.Replace(() => old);
            Assert.Throws<InvalidOperationException>(() => host.Replace(() => candidate));
            Assert.IsTrue(host.IsFaulted);
            Assert.IsNull(host.Sequential);
            Assert.AreEqual(1, old.Disposals);
            Assert.AreEqual(1, candidate.Disposals);
            Assert.Throws<InvalidOperationException>(() => host.AdvanceManual());
            host.Replace(() => new Counter(0));
            Assert.AreEqual(1, host.AdvanceManual().Tick);
        }

        [Test]
        public void FailedCandidatePreservesActiveFrameUntilSuccessfulReplacement()
        {
            using var host = new PlayHost<string, long>();
            var previous = new Counter(0);
            host.Replace(() => previous);
            host.Pump(1, false);
            host.AdvanceManual();
            var frames = host.Sequential;
            var active = frames.BeginNext();
            Assert.Throws<InvalidOperationException>(() => host.Replace(() => throw new InvalidOperationException()));
            var invalid = new Counter(0) { FailCapture = true };
            Assert.Throws<InvalidOperationException>(() => host.Replace(() => invalid));
            Assert.AreEqual(1, invalid.Disposals);
            Assert.True(frames.IsActive(active));
            Assert.AreEqual(2, host.AdvanceManual().Tick);
            var next = new Counter(0);
            host.Replace(() => next);
            Assert.AreEqual(0, host.Sequential.Presented.Tick);
            Assert.False(frames.Complete(active));
            Assert.AreEqual(1, previous.Disposals);
            host.Dispose();
            host.Dispose();
            Assert.AreEqual(1, next.Disposals);
            Assert.Throws<ObjectDisposedException>(() => host.Submit("stale"));
        }
    }
}
