using System;
using AiNative.Core;
using Xunit;

namespace AiNative.Tests
{
    public sealed class SessionStateTests
    {
        [Fact]
        public void Counters_TrackSubmissionCompletionRejectionAndEmptyAdvance()
        {
            var rules = new ProbeRules();
            var session = new SimulationSession<string, ulong>(rules, 42);
            Assert.Equal(0, session.ExportState().CompletedStepCount);
            Assert.Null(session.Advance());
            Assert.Equal(1, session.Submit("accept"));
            Assert.Equal(2, session.Submit("reject"));
            Assert.Equal(2, session.LastSubmittedSequence);
            Assert.Equal(0, session.CompletedStepCount);
            Assert.Throws<InvalidOperationException>(() => session.ExportState());
            Assert.Equal(2, session.PendingCount);
            Assert.False(session.IsFaulted);
            Assert.True(Assert.IsType<StepResult>(session.Advance()).Result.Succeeded);
            Assert.Equal(1, session.CompletedStepCount);
            Assert.Equal(1, session.PendingCount);
            Assert.False(Assert.IsType<StepResult>(session.Advance()).Result.Succeeded);
            SessionState saved = session.ExportState();
            Assert.Equal(2, saved.LastSubmittedSequence);
            Assert.Equal(2, saved.CompletedStepCount);
            Assert.Equal(42UL, saved.RandomState);
            Assert.Null(session.Advance());
            Assert.Equal(2, session.CompletedStepCount);
            Assert.Equal(2, rules.Executions);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(ulong.MaxValue)]
        public void Restore_AfterRandomConsumption_ContinuesWithoutReseeding(ulong seed)
        {
            var rules = new ProbeRules();
            var original = new SimulationSession<string, ulong>(rules, seed);
            for (int index = 0; index < 3; index++)
            {
                original.Submit("random");
                original.Advance();
            }
            SessionState saved = original.ExportState();
            Assert.Equal(unchecked(seed + 3UL * 0x9E3779B97F4A7C15UL), saved.RandomState);
            if (seed == 0)
            {
                Assert.Equal(0x06C45D188009454FUL, original.GetState());
            }
            var restoredRules = new ProbeRules { Value = original.GetState() };
            var restored = SimulationSession<string, ulong>.Restore(restoredRules, saved);
            Assert.Equal(0, restoredRules.Executions);
            Assert.Equal(0, restoredRules.Queries);
            Assert.Equal(saved.RandomState, restored.RandomState);
            Assert.Equal(0, restored.PendingCount);
            for (int index = 0; index < 5; index++)
            {
                Assert.Equal(4 + index, original.Submit("random"));
                Assert.Equal(4 + index, restored.Submit("random"));
                original.Advance();
                restored.Advance();
                Assert.Equal(original.GetState(), restored.GetState());
                Assert.Equal(original.RandomState, restored.RandomState);
                Assert.Equal(original.CompletedStepCount, restored.CompletedStepCount);
            }
            ulong restoredRandom = restored.RandomState;
            original.Submit("random");
            original.Advance();
            Assert.Equal(restoredRandom, restored.RandomState);
            Assert.Equal(3, saved.CompletedStepCount);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(ulong.MaxValue)]
        public void Restore_AllRandomStateBitsArePreserved(ulong randomState)
        {
            var state = new SessionState(0, 0, DeterministicRandom.Algorithm, randomState);
            var restored = SimulationSession<string, ulong>.Restore(new ProbeRules(), state);
            Assert.Equal(randomState, restored.ExportState().RandomState);
        }

        [Theory]
        [InlineData(-1, -1)]
        [InlineData(0, -1)]
        [InlineData(-1, 0)]
        [InlineData(1, 0)]
        [InlineData(0, 1)]
        public void State_InvalidCounters_AreRejected(long submitted, long completed)
        {
            Assert.Throws<ArgumentException>(() => new SessionState(submitted, completed, DeterministicRandom.Algorithm, 0));
        }

        [Theory]
        [InlineData("")]
        [InlineData("splitmix64-v2")]
        [InlineData("SPLITMIX64-V1")]
        public void State_UnsupportedAlgorithm_IsRejected(string algorithm)
        {
            Assert.Throws<ArgumentException>(() => new SessionState(0, 0, algorithm, 0));
        }

        [Fact]
        public void Restore_NullArguments_AreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new SessionState(0, 0, null!, 0));
            Assert.Throws<ArgumentNullException>(() => SimulationSession<string, ulong>.Restore(new ProbeRules(), null!));
            Assert.Throws<ArgumentNullException>(() => SimulationSession<string, ulong>.Restore(null!,
                new SessionState(0, 0, DeterministicRandom.Algorithm, 0)));
        }

        [Fact]
        public void Submit_AtSequenceLimit_LeavesHealthyStateAndQueueUnchanged()
        {
            var session = SimulationSession<string, ulong>.Restore(new ProbeRules(),
                new SessionState(long.MaxValue - 1, long.MaxValue - 1, DeterministicRandom.Algorithm, 0));
            Assert.Equal(long.MaxValue, session.Submit("accept"));
            session.Advance();
            Assert.Throws<OverflowException>(() => session.Submit("accept"));
            Assert.False(session.IsFaulted);
            Assert.Equal(0, session.PendingCount);
            Assert.Equal(long.MaxValue, session.ExportState().CompletedStepCount);
            Assert.Null(session.Advance());
        }

        [Theory]
        [InlineData("throw")]
        [InlineData("null")]
        [InlineData("consumeThenReject")]
        public void Advance_WhenFaulted_DoesNotCompleteStepOrAllowExport(string command)
        {
            var session = new SimulationSession<string, ulong>(new ProbeRules(), 0);
            session.Submit(command);
            session.Submit("later");
            Assert.Throws<InvalidOperationException>(() => session.Advance());
            Assert.True(session.IsFaulted);
            Assert.Equal(2, session.LastSubmittedSequence);
            Assert.Equal(0, session.CompletedStepCount);
            Assert.Equal(1, session.PendingCount);
            Assert.Throws<InvalidOperationException>(() => session.ExportState());
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ReentrantCalls_AreRejectedWithoutExecutingNestedOperations(bool duringQuery, bool catchReentry)
        {
            foreach (string operation in new[] { "submit", "advance", "query", "export" })
            {
                var rules = new ProbeRules();
                var session = new SimulationSession<string, ulong>(rules, 42);
                Action nested = operation switch
                {
                    "submit" => () => session.Submit("nested"),
                    "advance" => () => session.Advance(),
                    "query" => () => session.GetState(),
                    _ => () => session.ExportState()
                };
                Action callback = () =>
                {
                    if (catchReentry)
                    {
                        Assert.Throws<InvalidOperationException>(nested);
                    }
                    else
                    {
                        nested();
                    }
                };
                if (duringQuery)
                {
                    rules.OnQuery = callback;
                }
                else
                {
                    rules.OnExecute = callback;
                }
                session.Submit("accept");
                Action outer = duringQuery ? () => session.GetState() : () => session.Advance();
                if (catchReentry)
                {
                    outer();
                    Assert.False(session.IsFaulted);
                    rules.OnQuery = null;
                    rules.OnExecute = null;
                    if (duringQuery)
                    {
                        session.Advance();
                    }
                    Assert.Equal(1, session.ExportState().CompletedStepCount);
                    Assert.Equal(2, session.Submit("next"));
                    session.Advance();
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(outer);
                    Assert.True(session.IsFaulted);
                    Assert.Equal(0, session.CompletedStepCount);
                    Assert.Throws<InvalidOperationException>(() => session.ExportState());
                }
                Assert.Equal(42UL, session.RandomState);
            }
        }

        private sealed class ProbeRules : IGameRules<string, ulong>
        {
            public ulong Value { get; set; }
            public int Executions { get; private set; }
            public int Queries { get; private set; }
            public Action? OnExecute { get; set; }
            public Action? OnQuery { get; set; }

            public CommandResult Execute(string command, DeterministicRandom random)
            {
                Executions++;
                OnExecute?.Invoke();
                if (command == "throw")
                {
                    throw new InvalidOperationException("test failure");
                }
                if (command == "null")
                {
                    return null!;
                }
                if (command == "random" || command == "consumeThenReject")
                {
                    Value = random.NextUInt64();
                }
                return command == "reject" || command == "consumeThenReject"
                    ? CommandResult.Rejected("Invalid") : CommandResult.Accepted();
            }

            public ulong GetState()
            {
                Queries++;
                OnQuery?.Invoke();
                return Value;
            }
        }
    }
}
