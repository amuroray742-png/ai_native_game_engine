using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class HostBoundaryTests
    {
        [Theory]
        [InlineData("A,A")]
        [InlineData("A,B")]
        [InlineData(" A ")]
        [InlineData("0")]
        public void InvalidSide_IsRejectedBySharedLegacyAndBatchReaders(string side)
        {
            using var directory = new SnapshotDirectory();
            JsonNode scene = JsonNode.Parse(SnapshotFixtures.Read("scenario.json"))!;
            scene["units"]![0]!["side"] = side;
            byte[] bytes = Encoding.UTF8.GetBytes(scene.ToJsonString());
            ProtocolException error = Assert.Throws<ProtocolException>(() => ScenarioCodec.Decode(bytes));
            Assert.Equal("InvalidProtocol", error.Code);
            Assert.Equal("$.units[0].side", error.Path);
            File.WriteAllBytes(directory.FilePath, bytes);
            Assert.Throws<JsonException>(() => ScenarioLoader.Load(directory.FilePath));
            Assert.Equal(2, Program.Run(new[] { directory.FilePath }, new StringWriter(), new StringWriter()));
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            var result = CliTests.Invoke(new[] { "run", "--scenario", directory.FilePath, "--seed", "42", "--commands", commands });
            Assert.Equal(2, result.Code);
            Assert.Equal("InvalidProtocol", result.Result["code"]!.GetValue<string>());
            Assert.Null(result.Result["initialCheckpoint"]);
        }

        [Fact]
        public void LowercaseScenario_IsAcceptedAndCanonicalizedByAllReaders()
        {
            using var directory = new SnapshotDirectory();
            JsonNode scene = JsonNode.Parse(SnapshotFixtures.Read("scenario.json"))!;
            scene["units"]![0]!["side"] = "a";
            scene["units"]![1]!["side"] = "b";
            byte[] bytes = Encoding.UTF8.GetBytes(scene.ToJsonString());
            File.WriteAllBytes(directory.FilePath, bytes);
            Assert.Equal(CanonicalState.ConfigurationBytes(Fixtures.Definition()),
                CanonicalState.ConfigurationBytes(ScenarioCodec.Decode(bytes)));
            Assert.Equal(0, Program.Run(new[] { directory.FilePath }, new StringWriter(), new StringWriter()));
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            Assert.Equal(0, CliTests.Invoke(new[] { "run", "--scenario", directory.FilePath, "--seed", "42", "--commands", commands }).Code);
        }

        [Fact]
        public void Runtime_StepAndSnapshotStayIsolatedFromFurtherPlay()
        {
            GridGameRuntime game = GridGameRuntime.Create(Fixtures.Definition(), 42);
            game.Submit(new Move(Side.A, 1, MoveDirection.Up));
            game.Submit(new Move(Side.A, 1, MoveDirection.Up));
            GridStep first = Assert.IsType<GridStep>(game.Advance());
            Assert.Equal(1, first.Step.Sequence);
            Assert.Equal(1, first.State.PendingCount);
            Assert.Throws<InvalidOperationException>(() => game.ExportState());
            GridStep second = Assert.IsType<GridStep>(game.Advance());
            Assert.Equal(new GridPosition(0, 1), first.State.Game.Units[0].Position);
            Assert.Equal(new GridPosition(0, 2), second.State.Game.Units[0].Position);
            GridGameSnapshot snapshot = game.ExportState();
            GridGameRuntime restored = GridGameRuntime.Restore(snapshot);
            Assert.False(game.Capture().IsFromSameGame(restored.Capture()));
            restored.Submit(new EndTurn(Side.A));
            restored.Advance();
            Assert.Equal(Side.A, snapshot.Game.ActiveSide);
            Assert.Equal(Side.A, game.GetState().ActiveSide);
            Assert.Equal(Side.B, restored.GetState().ActiveSide);
            Assert.Equal(2, snapshot.Session.CompletedStepCount);
        }

        [Fact]
        public void Runtime_InvalidRestoreCannotReplaceOrChangeExistingGame()
        {
            GridGameRuntime game = GridGameRuntime.Create(Fixtures.Definition(), 42);
            GridGameSnapshot snapshot = game.ExportState();
            var invalid = new GridGameSnapshot(snapshot.Session, new GridGameState(snapshot.Game.Configuration,
                snapshot.Game.Units, Side.A, 2, 3, null));
            Assert.Throws<ArgumentException>(() => GridGameRuntime.Restore(invalid));
            Assert.Equal(1, game.GetState().TurnIndex);
            Assert.Equal(1, game.Submit(new Move(Side.A, 1, MoveDirection.Up)));
            Assert.True(Assert.IsType<GridStep>(game.Advance()).Step.Result.Succeeded);
        }

        [Fact]
        public void PortableRecord_MatchesIndependentCliRunAcrossDifferentSourceLabels()
        {
            using var directory = new SnapshotDirectory();
            byte[] commands = SnapshotFixtures.Read("commands.json");
            var alternate = new HostEnvironment("adapter-test-runtime", "test-os", "test-architecture", new string('a', 64));
            byte[] portable = HostVerification.Record(ScenarioCodec.Decode(SnapshotFixtures.Read("scenario.json")), 42, commands, alternate);
            string commandPath = CliTests.WriteCommands(directory, commands);
            string replayPath = Path.Combine(directory.Path, "cli.replay.json");
            Assert.Equal(0, CliTests.Invoke(CliTests.RunArgs(commandPath).Concat(new[] { "--replay-out", replayPath }).ToArray()).Code);
            HostComparison comparison = HostVerification.Compare(File.ReadAllBytes(replayPath), portable);
            Assert.True(comparison.Matched);
            Assert.NotEqual(comparison.ExpectedEnvironment.RuntimeVersion, comparison.ActualEnvironment.RuntimeVersion);
            Assert.Equal(CommandFile.Decode(commands).Operations.Count + 1, comparison.ComparedCheckpoints);
            Assert.Empty(comparison.Differences);
            JsonNode snapshot = JsonNode.Parse(portable)!["initialSnapshot"]!;
            Assert.Equal("EnvironmentMismatch", Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                Encoding.UTF8.GetBytes(snapshot.ToJsonString()), SnapshotEnvironment.Current())).Code);
        }

        [Fact]
        public void Compare_ValidEventDeviationReportsFirstDifferenceWithoutRestoringEnvironment()
        {
            HostComparison result = HostVerification.Compare(SnapshotFixtures.Read("replay.json"),
                SnapshotFixtures.Read("event-divergence.replay.json"));
            Assert.False(result.Matched);
            Assert.Equal(5, result.OperationIndex);
            Assert.Equal(2, result.Sequence);
            Assert.Equal(6, result.ComparedCheckpoints);
            Assert.Contains(result.Differences, item => item.Path == "result.events[0].to.x");
        }

        [Theory]
        [InlineData("commands")]
        [InlineData("configuration")]
        [InlineData("seed")]
        public void Compare_DifferentExperimentInputsAreNotReportedAsRuleDivergences(string change)
        {
            ScenarioDefinition original = Fixtures.Definition();
            ScenarioDefinition configuration = change == "configuration"
                ? new ScenarioDefinition(original.Width, original.Height, original.Obstacles, original.Units, 2, 3) : original;
            byte[] commands = SnapshotFixtures.Read("commands.json");
            byte[] changedCommands = change == "commands" ? SnapshotFixtures.Read("empty-commands.json") : commands;
            byte[] expected = HostVerification.Record(original, 42, commands, SnapshotFixtures.Environment);
            byte[] actual = HostVerification.Record(configuration, change == "seed" ? 43UL : 42UL,
                changedCommands, SnapshotFixtures.Environment);
            Assert.Equal("ComparisonInputMismatch", Assert.Throws<ProtocolException>(() => HostVerification.Compare(expected, actual)).Code);
        }

        [Fact]
        public void Compare_DamagedCheckpointIsRejectedBeforeComparison()
        {
            JsonNode broken = JsonNode.Parse(SnapshotFixtures.Read("replay.json"))!;
            broken["checkpoints"]![0]!["stateHash"] = new string('0', 64);
            Assert.Equal("InvalidReplay", Assert.Throws<ProtocolException>(() => HostVerification.Compare(
                SnapshotFixtures.Read("replay.json"), Encoding.UTF8.GetBytes(broken.ToJsonString()))).Code);
        }

        [Fact]
        public void CompareCli_ReportsEvidenceComparisonWithoutClaimingExecution()
        {
            using var directory = new SnapshotDirectory();
            string expected = Path.Combine(directory.Path, "expected.json");
            string actual = Path.Combine(directory.Path, "actual.json");
            File.WriteAllBytes(expected, SnapshotFixtures.Read("replay.json"));
            File.WriteAllBytes(actual, SnapshotFixtures.Read("replay.json"));
            string[] args = { "compare", "--expected", expected, "--actual", actual };
            var matched = CliTests.Invoke(args);
            Assert.Equal(0, matched.Code);
            Assert.Equal("HostTracesMatched", matched.Result["code"]!.GetValue<string>());
            Assert.Null(matched.Result["initialCheckpoint"]);
            Assert.Empty(matched.Result["checkpoints"]!.AsArray());
            Assert.Equal(0, matched.Result["accepted"]!.GetValue<int>());
            File.WriteAllBytes(actual, SnapshotFixtures.Read("event-divergence.replay.json"));
            var diverged = CliTests.Invoke(args);
            Assert.Equal(3, diverged.Code);
            Assert.Equal(5, diverged.Result["location"]!["operationIndex"]!.GetValue<int>());
            File.WriteAllText(actual, "{}");
            Assert.Equal(2, CliTests.Invoke(args).Code);
        }

        [Theory]
        [InlineData("compare")]
        [InlineData("compare --expected a")]
        [InlineData("compare --expected a --actual b --actual c")]
        [InlineData("compare --expected a --actual b --seed 42")]
        public void CompareCli_InvalidArgumentsFailBeforeReadingFiles(string args)
        {
            var result = CliTests.Invoke(args.Split(' '));
            Assert.Equal(2, result.Code);
            Assert.Equal("InvalidArguments", result.Result["code"]!.GetValue<string>());
        }

        [Fact]
        public void PortableLibraries_HaveNoHostOrUnityAssemblyDependency()
        {
            foreach (Type type in new[] { typeof(SimulationSession<,>), typeof(GridGameRuntime), typeof(HostVerification) })
            {
                Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(), name => name.Name == "AiNative.Headless"
                    || name.Name!.StartsWith("Unity", StringComparison.Ordinal));
            }
        }
    }
}
