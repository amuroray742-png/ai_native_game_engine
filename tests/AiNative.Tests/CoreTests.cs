using System;
using System.Collections.Generic;
using System.Linq;
using AiNative.Core;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CoreTests
    {
        [Fact]
        public void EmptyTickAndBatchedInputs_HaveIndependentCountersAndStableOrder()
        {
            using var runtime = Create();
            Assert.Equal(1, runtime.Advance().Tick);
            Assert.Equal(0, runtime.Processed);
            Assert.Equal(1, runtime.Submit("first"));
            Assert.Equal(2, runtime.Submit("reject"));
            Assert.Equal(3, runtime.Submit("second"));
            Assert.Throws<InvalidOperationException>(() => runtime.ExportState());
            Assert.Equal(3, runtime.PendingCount);
            TickResult<string[]> frame = runtime.Advance();
            Assert.Equal(2, frame.Tick);
            Assert.Equal(new long[] { 1, 2, 3 }, frame.Inputs.Select(input => input.Sequence));
            Assert.Equal(new[] { "first", "second" }, frame.Observation.State);
            Assert.Equal("Rejected", frame.Inputs[1].Result.Error);
            Assert.Equal(3, runtime.Processed);
            Assert.Equal(0, runtime.PendingCount);
            runtime.Submit("later");
            runtime.Advance();
            Assert.Equal(new[] { "first", "second" }, frame.Observation.State);
        }

        [Theory]
        [InlineData("throw")]
        [InlineData("consumeThenReject")]
        [InlineData("null")]
        public void InvalidRuleResult_FaultsAndPreventsFurtherUse(string command)
        {
            var simulation = new RecordingSimulation();
            using var runtime = Create(simulation);
            runtime.Submit(command);
            runtime.Submit("later");
            Assert.Throws<InvalidOperationException>(() => runtime.Advance());
            Assert.True(runtime.IsFaulted);
            Assert.Equal(0, runtime.Tick);
            Assert.Throws<InvalidOperationException>(() => runtime.Advance());
            Assert.Throws<InvalidOperationException>(() => runtime.Capture());
            Assert.Throws<InvalidOperationException>(() => runtime.CaptureGameState());
            Assert.Throws<InvalidOperationException>(() => runtime.ExportState());
            Assert.Throws<InvalidOperationException>(() => runtime.Submit("new"));
            runtime.Dispose();
            runtime.Dispose();
            Assert.Equal(1, simulation.Disposals);
            Assert.Throws<ObjectDisposedException>(() => runtime.Advance());
        }

        [Theory]
        [InlineData("input")]
        [InlineData("tick")]
        [InlineData("query")]
        [InlineData("export")]
        public void Reentry_IsRejectedAtEverySimulationCallback(string phase)
        {
            var simulation = new RecordingSimulation();
            using var runtime = Create(simulation);
            simulation.Callback = current =>
            {
                if (current != phase)
                {
                    return;
                }
                Assert.Throws<InvalidOperationException>(() => runtime.Advance());
                Assert.Throws<InvalidOperationException>(() => runtime.Submit("nested"));
                Assert.Throws<InvalidOperationException>(() => runtime.GetState());
                Assert.Throws<InvalidOperationException>(() => runtime.Dispose());
            };
            runtime.Submit("first");
            runtime.Advance();
            runtime.ExportState();
            Assert.False(runtime.IsFaulted);
            Assert.Equal(1, runtime.Processed);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(42UL)]
        [InlineData(9223372036854775808UL)]
        [InlineData(ulong.MaxValue)]
        public void Restore_PreservesRandomBitsAndContinuesWithoutReseeding(ulong seed)
        {
            using var original = Create(seed: seed);
            original.Submit("random");
            original.Advance();
            original.Advance();
            SimulationSnapshot<string[]> snapshot = original.ExportState();
            using var restored = SimulationRuntime<string, string[], string[]>.Restore(snapshot, data => new RecordingSimulation(data));
            Assert.Equal(original.RandomState, restored.RandomState);
            Assert.Equal(2, restored.Tick);
            original.Submit("random");
            restored.Submit("random");
            original.Advance();
            restored.Advance();
            Assert.Equal(original.GetState(), restored.GetState());
            Assert.Equal(original.RandomState, restored.RandomState);
            Assert.Equal(2, restored.Processed);
            Assert.Equal(3, restored.Tick);
            using var independent = Create(seed: seed);
            Assert.Equal(seed, independent.RandomState);
        }

        [Fact]
        public void Random_HasIndependentFixedVectors()
        {
            var random = new DeterministicRandom(0);
            foreach (ulong expected in new[] { 0xE220A8397B1DCDAFUL, 0x6E789E6AA1B965F4UL, 0x06C45D188009454FUL })
            {
                Assert.Equal(expected, random.NextUInt64());
            }
            Assert.Equal(unchecked(3UL * 0x9E3779B97F4A7C15UL), random.State);
        }

        [Theory]
        [InlineData(-1, 0, 0)]
        [InlineData(0, 1, 1)]
        [InlineData(1, 2, 1)]
        [InlineData(1, -1, -1)]
        [InlineData(1, 0, 1)]
        public void InvalidSaveCounters_AreRejected(long tick, long submitted, long processed) =>
            Assert.Throws<ArgumentException>(() => new RuntimeState(tick, submitted, processed, 0, new SimulationTiming(0)));

        [Fact]
        public void LimitsAndNullInput_DoNotSilentlyWrap()
        {
            using var runtime = Create();
            Assert.Throws<ArgumentNullException>(() => runtime.Submit(null!));
            Assert.Equal(0, runtime.Submitted);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationTiming(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationTiming(1001));
            var state = new SimulationSnapshot<string[]>(new RuntimeState(1, long.MaxValue, long.MaxValue, 0, new SimulationTiming(0)), Array.Empty<string>());
            using var full = SimulationRuntime<string, string[], string[]>.Restore(state, data => new RecordingSimulation(data));
            Assert.Throws<OverflowException>(() => full.Submit("next"));
            Assert.False(full.IsFaulted);
            Assert.Equal(0, full.PendingCount);
            var end = new SimulationSnapshot<string[]>(new RuntimeState(long.MaxValue, 0, 0, 0, new SimulationTiming(0)), Array.Empty<string>());
            using var last = SimulationRuntime<string, string[], string[]>.Restore(end, data => new RecordingSimulation(data));
            Assert.Throws<OverflowException>(() => last.Advance());
            Assert.True(last.IsFaulted);
        }

        [Theory]
        [InlineData("tick")]
        [InlineData("query")]
        [InlineData("export")]
        public void UnexpectedCallbackFailure_FaultsInstance(string phase)
        {
            var simulation = new RecordingSimulation();
            using var runtime = Create(simulation);
            simulation.Callback = current => { if (current == phase) { throw new InvalidOperationException("injected"); } };
            if (phase == "export")
            {
                Assert.Throws<InvalidOperationException>(() => runtime.ExportState());
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => runtime.Advance());
            }
            Assert.True(runtime.IsFaulted);
        }

        internal static SimulationRuntime<string, string[], string[]> Create(RecordingSimulation? simulation = null, ulong seed = 0) =>
            SimulationRuntime<string, string[], string[]>.Create(() => simulation ?? new RecordingSimulation(), seed, new SimulationTiming(60));
    }

    internal sealed class RecordingSimulation : IGameSimulation<string, string[], string[]>
    {
        private readonly List<string> _values;
        internal RecordingSimulation(string[]? values = null)
        {
            _values = new List<string>(values ?? Array.Empty<string>());
        }
        internal Action<string>? Callback { get; set; }
        internal int Disposals { get; private set; }
        public CommandResult Execute(string command, DeterministicRandom random)
        {
            Callback?.Invoke("input");
            if (command == "throw")
            {
                throw new InvalidOperationException("injected");
            }
            if (command == "null")
            {
                return null!;
            }
            if (command == "consumeThenReject")
            {
                random.NextUInt64();
                return CommandResult.Rejected("Rejected");
            }
            if (command == "reject")
            {
                return CommandResult.Rejected("Rejected");
            }
            _values.Add(command == "random" ? random.NextUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : command);
            return CommandResult.Accepted(new RecordedEvent(command));
        }
        public IReadOnlyList<GameEvent> Advance(StepContext context)
        {
            Callback?.Invoke("tick");
            return Array.Empty<GameEvent>();
        }
        public string[] GetState()
        {
            Callback?.Invoke("query");
            return _values.ToArray();
        }
        public string[] ExportState()
        {
            Callback?.Invoke("export");
            return _values.ToArray();
        }
        public void Dispose()
        {
            Disposals++;
        }
    }
    internal sealed class RecordedEvent : GameEvent
    {
        internal RecordedEvent(string value)
        {
            Value = value;
        }
        internal string Value { get; }
    }
}
