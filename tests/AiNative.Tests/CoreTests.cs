using System;
using System.Collections.Generic;
using AiNative.Core;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CoreTests
    {
        [Fact]
        public void Submit_ThenAdvance_ExecutesOneCommandInFifoOrder()
        {
            var rules = new RecordingRules();
            var session = new SimulationSession<string, string[]>(rules, 0);
            Assert.Null(session.Advance());
            Assert.Equal(1, session.Submit("first"));
            Assert.Equal(2, session.Submit("second"));
            Assert.Empty(session.GetState());
            Assert.Equal(1, Assert.IsType<StepResult>(session.Advance()).Sequence);
            Assert.Equal(new[] { "first" }, session.GetState());
            Assert.Equal(1, session.PendingCount);
            Assert.Equal(2, Assert.IsType<StepResult>(session.Advance()).Sequence);
            Assert.Equal(new[] { "first", "second" }, session.GetState());
            Assert.Null(session.Advance());
            Assert.Equal(2, rules.Executions);
        }

        [Theory]
        [InlineData("throw")]
        [InlineData("consumeThenReject")]
        public void Advance_WhenRulesBreakContract_FaultsAndStopsSession(string command)
        {
            var session = new SimulationSession<string, string[]>(new RecordingRules(), 0);
            session.Submit(command);
            session.Submit("later");
            Assert.Throws<InvalidOperationException>(() => session.Advance());
            Assert.True(session.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => session.Advance());
            Assert.Throws<InvalidOperationException>(() => session.Submit("new"));
            Assert.Throws<InvalidOperationException>(() => session.GetState());
        }

        [Fact]
        public void Random_SeedZero_MatchesPublishedSplitMix64Sequence()
        {
            // 固定向量，不能通过被测实现现场计算期望值。
            ulong[] expected = { 0xE220A8397B1DCDAFUL, 0x6E789E6AA1B965F4UL, 0x06C45D188009454FUL };
            var random = new DeterministicRandom(0);
            Assert.Equal("splitmix64-v1", DeterministicRandom.Algorithm);
            foreach (ulong value in expected)
            {
                Assert.Equal(value, random.NextUInt64());
            }
            Assert.Equal(unchecked(3UL * 0x9E3779B97F4A7C15UL), random.State);
        }

        [Fact]
        public void Sessions_WithSameSeed_HaveIndependentRandomStates()
        {
            var first = new SimulationSession<string, string[]>(new RecordingRules(), ulong.MaxValue);
            var second = new SimulationSession<string, string[]>(new RecordingRules(), ulong.MaxValue);
            first.Submit("random");
            first.Advance();
            Assert.Equal(ulong.MaxValue, second.RandomState);
            second.Submit("random");
            second.Advance();
            Assert.Equal(first.GetState(), second.GetState());
            Assert.Equal(first.RandomState, second.RandomState);
        }

        [Fact]
        public void Submit_Null_DoesNotEnqueueOrFault()
        {
            var session = new SimulationSession<string, string[]>(new RecordingRules(), 0);
            Assert.Throws<ArgumentNullException>(() => session.Submit(null!));
            Assert.Equal(0, session.PendingCount);
            Assert.False(session.IsFaulted);
        }

        private sealed class RecordingRules : IGameRules<string, string[]>
        {
            private readonly List<string> _values = new List<string>();
            public int Executions
            {
                get; private set;
            }

            public CommandResult Execute(string command, DeterministicRandom random)
            {
                Executions++;
                if (command == "throw")
                {
                    throw new InvalidOperationException("test failure");
                }
                if (command == "consumeThenReject")
                {
                    random.NextUInt64();
                    return CommandResult.Rejected("Invalid");
                }
                _values.Add(command == "random" ? random.NextUInt64().ToString() : command);
                return CommandResult.Accepted();
            }

            public string[] GetState() => _values.ToArray();
        }
    }
}
