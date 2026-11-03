using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CliTests
    {
        [Fact]
        public void Run_FixedCommandFile_MatchesAllDocumentedCheckpointsAndResults()
        {
            using var directory = new SnapshotDirectory();
            string commands = WriteCommands(directory, SnapshotFixtures.Read("commands.json"));
            (int code, JsonObject result, string errors) = Invoke(RunArgs(commands));
            Assert.Equal(0, code);
            Assert.Equal("", errors);
            JsonObject expected = JsonNode.Parse(SnapshotFixtures.Read("run-result.json"))!.AsObject();
            Assert.Equal(expected.Select(item => item.Key).OrderBy(key => key), result.Select(item => item.Key).OrderBy(key => key));
            foreach (var property in expected.Where(property => property.Key != "message"))
            {
                Assert.True(JsonNode.DeepEquals(property.Value, result[property.Key]), property.Key);
            }
            for (int count = 0; count < 9; count++)
            {
                Assert.True(JsonNode.DeepEquals(result, Invoke(RunArgs(commands)).Result));
            }
        }

        [Theory]
        [InlineData("run")]
        [InlineData("inspect")]
        [InlineData("run --scenario a --commands b")]
        [InlineData("run --snapshot a --seed 42 --commands b")]
        [InlineData("run --scenario a --snapshot b --seed 42 --commands c")]
        [InlineData("run --scenario a --seed 42 --commands b --expect-config c")]
        [InlineData("run --scenario a --seed 42 --commands b --commands c")]
        [InlineData("run --scenario a --seed 42 --commands")]
        [InlineData("run --scenario=a --seed 42 --commands b")]
        [InlineData("run --scenario a --seed 042 --commands b")]
        [InlineData("run --scenario a --seed -1 --commands b")]
        [InlineData("run --scenario a --seed +1 --commands b")]
        [InlineData("run --scenario a --seed 18446744073709551616 --commands b")]
        [InlineData("run --scenario a --seed 1e1 --commands b")]
        [InlineData("run --scenario a --seed 42 --commands b --unknown c")]
        [InlineData("inspect --snapshot a --commands b")]
        [InlineData("inspect --snapshot a --snapshot-out b")]
        [InlineData("inspect --snapshot a --seed 42")]
        [InlineData("replay --input a --seed 42")]
        [InlineData("run --help --scenario a")]
        public void InvalidArguments_ReturnStructuredFailureBeforeReadingFiles(string arguments)
        {
            (int code, JsonObject result, string errors) = Invoke(arguments.Split(' '));
            Assert.Equal(2, code);
            Assert.Equal("InputError", result["category"]!.GetValue<string>());
            Assert.Equal("InvalidArguments", result["code"]!.GetValue<string>());
            Assert.Null(result["initialCheckpoint"]);
            Assert.Empty(result["checkpoints"]!.AsArray());
            Assert.Contains("InvalidArguments", errors);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("18446744073709551615")]
        public void EmptyRun_AndInspect_PreserveSeedAndDoNotExecute(string seed)
        {
            using var directory = new SnapshotDirectory();
            string commands = WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            string[] args = { "run", "--scenario", Fixtures.DemoPath, "--seed", seed, "--commands", commands, "--snapshot-out", directory.FilePath };
            var run = Invoke(args);
            Assert.Equal(0, run.Code);
            Assert.Empty(run.Result["checkpoints"]!.AsArray());
            Assert.Equal(seed, run.Result["initialCheckpoint"]!["state"]!["random"]!["state"]!.GetValue<string>());
            Assert.Equal(directory.FilePath, run.Result["artifacts"]![0]!["path"]!.GetValue<string>());
            var inspect = Invoke(new[] { "inspect", "--snapshot", directory.FilePath, "--expect-config", Fixtures.DemoPath });
            Assert.Equal(0, inspect.Code);
            Assert.Equal("SnapshotInspected", inspect.Result["code"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(run.Result["initialCheckpoint"], inspect.Result["initialCheckpoint"]));
            Assert.True(JsonNode.DeepEquals(inspect.Result["initialCheckpoint"], inspect.Result["finalCheckpoint"]));
            Assert.Empty(inspect.Result["artifacts"]!.AsArray());
        }

        [Fact]
        public void Run_RejectsPendingQueueBeforeSimulationOrPublishing()
        {
            using var directory = new SnapshotDirectory();
            string commands = WriteCommands(directory, Encoding.UTF8.GetBytes("""
                {"commandFormatVersion":1,"operations":[{"op":"submit","command":{"type":"EndTurn","side":"A"}}]}
                """));
            var call = Invoke(RunArgs(commands).Concat(new[] { "--snapshot-out", directory.FilePath }).ToArray());
            Assert.Equal(2, call.Code);
            Assert.Equal("PendingCommands", call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
            Assert.False(File.Exists(directory.FilePath));
        }

        [Fact]
        public void Run_OutputPreflightFailure_IsHostFailureWithNoExecutedCheckpoint()
        {
            using var directory = new SnapshotDirectory();
            string commands = WriteCommands(directory, SnapshotFixtures.Read("commands.json"));
            File.WriteAllText(directory.FilePath, "original");
            var exists = Invoke(RunArgs(commands).Concat(new[] { "--snapshot-out", directory.FilePath }).ToArray());
            Assert.Equal(1, exists.Code);
            Assert.Equal("HostFailure", exists.Result["category"]!.GetValue<string>());
            Assert.Equal("OutputExists", exists.Result["code"]!.GetValue<string>());
            Assert.Null(exists.Result["initialCheckpoint"]);
            Assert.Equal("original", File.ReadAllText(directory.FilePath));
            var missing = Invoke(RunArgs(commands).Concat(new[] { "--snapshot-out", Path.Combine(directory.Path, "missing", "state.json") }).ToArray());
            Assert.Equal("OutputWriteFailed", missing.Result["code"]!.GetValue<string>());
        }

        [Fact]
        public void Run_ValidNegativeEntityId_IsBusinessRejectionAndContinues()
        {
            using var directory = new SnapshotDirectory();
            string commands = WriteCommands(directory, Encoding.UTF8.GetBytes("""
                {"commandFormatVersion":1,"operations":[
                  {"op":"submit","command":{"type":"Attack","side":"A","unitId":-1,"targetId":2}},
                  {"op":"advance"}, {"op":"submit","command":{"type":"EndTurn","side":"A"}}, {"op":"advance"}]}
                """));
            var call = Invoke(RunArgs(commands));
            Assert.Equal(0, call.Code);
            Assert.Equal(1, call.Result["rejected"]!.GetValue<int>());
            Assert.Equal(1, call.Result["accepted"]!.GetValue<int>());
            Assert.Equal("UnknownUnit", call.Result["checkpoints"]![1]!["result"]!["errorCode"]!.GetValue<string>());
            Assert.Equal(call.Result["initialCheckpoint"]!["stateHash"]!.GetValue<string>(), call.Result["checkpoints"]![1]!["stateHash"]!.GetValue<string>());
        }

        [Fact]
        public void Inspect_ExpectedConfigurationMismatch_AndRunStructureBeforeEnvironment_AreInputErrors()
        {
            using var directory = new SnapshotDirectory();
            GridGameRuntime session = GridGameRuntime.Create(Fixtures.Definition(), 42);
            new SnapshotStore().Save(directory.FilePath, session);
            JsonObject configuration = JsonNode.Parse(SnapshotFixtures.Read("scenario.json"))!.AsObject();
            configuration["attackDamage"] = 3;
            string expected = Path.Combine(directory.Path, "expected.json");
            File.WriteAllText(expected, configuration.ToJsonString());
            var inspect = Invoke(new[] { "inspect", "--snapshot", directory.FilePath, "--expect-config", expected });
            Assert.Equal(2, inspect.Code);
            Assert.Equal("ExpectedConfigMismatch", inspect.Result["code"]!.GetValue<string>());
            Assert.Null(inspect.Result["initialCheckpoint"]);
            File.WriteAllBytes(directory.FilePath, SnapshotFixtures.Read("initial.snapshot.json"));
            string commands = WriteCommands(directory, Encoding.UTF8.GetBytes("{"));
            var run = Invoke(new[] { "run", "--snapshot", directory.FilePath, "--commands", commands });
            Assert.Equal(2, run.Code);
            Assert.Equal("InvalidJson", run.Result["code"]!.GetValue<string>());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Run_SequenceOrRuleOverflow_HasDistinctFailureAndLastTrustedCheckpoint(bool ruleFault)
        {
            using var directory = new SnapshotDirectory();
            ScenarioDefinition config = Fixtures.Definition();
            GridGameRules rules = ruleFault
                ? GridGameRules.Restore(new GridGameState(config, config.Units, Side.A, 2, long.MaxValue, null))
                : GridGameRules.Create(config);
            long count = ruleFault ? long.MaxValue - 1 : long.MaxValue;
            GridGameRuntime session = GridGameRuntime.Restore(new GridGameSnapshot(
                new SessionState(count, count, DeterministicRandom.Algorithm, 42), rules.ExportState()));
            new SnapshotStore().Save(directory.FilePath, session);
            string commands = WriteCommands(directory, Encoding.UTF8.GetBytes("""
                {"commandFormatVersion":1,"operations":[{"op":"submit","command":{"type":"EndTurn","side":"A"}},{"op":"advance"}]}
                """));
            string target = Path.Combine(directory.Path, "after.json");
            string replayTarget = Path.Combine(directory.Path, "after.replay.json");
            var call = Invoke(new[] { "run", "--snapshot", directory.FilePath, "--commands", commands, "--snapshot-out", target, "--replay-out", replayTarget });
            Assert.Equal(1, call.Code);
            Assert.Equal("SimulationFailure", call.Result["category"]!.GetValue<string>());
            Assert.Equal(ruleFault ? "SessionFaulted" : "SequenceOverflow", call.Result["code"]!.GetValue<string>());
            Assert.Equal(ruleFault ? 1 : 0, call.Result["location"]!["operationIndex"]!.GetValue<int>());
            Assert.Equal(ruleFault ? 1 : 0, call.Result["checkpoints"]!.AsArray().Count);
            Assert.Equal(0, call.Result["accepted"]!.GetValue<int>());
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(replayTarget));
            if (ruleFault)
            {
                Assert.Equal(long.MaxValue.ToString(), call.Result["location"]!["sequence"]!.GetValue<string>());
                Assert.Equal("submitted", call.Result["finalCheckpoint"]!["result"]!["kind"]!.GetValue<string>());
            }
        }

        [Fact]
        public void OutputFailure_DoesNotRollbackPublishedSnapshot_AndBrokenDiagnosticsDoNotEscape()
        {
            using var directory = new SnapshotDirectory();
            string commands = WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            var error = new StringWriter();
            Assert.Equal(1, Program.Run(RunArgs(commands).Concat(new[] { "--snapshot-out", directory.FilePath }).ToArray(), new BrokenWriter(), error));
            Assert.Contains("StandardOutputFailed", error.ToString());
            Assert.True(File.Exists(directory.FilePath));
            var output = new StringWriter();
            Assert.Equal(2, Program.Run(new[] { "run" }, output, new BrokenWriter()));
            Assert.Equal("InvalidArguments", JsonNode.Parse(output.ToString())!["code"]!.GetValue<string>());
            Assert.Equal(1, Program.Run(new[] { "run" }, new BrokenWriter(), new BrokenWriter()));
        }

        [Theory]
        [InlineData("--help")]
        [InlineData("run --help")]
        [InlineData("inspect --help")]
        [InlineData("replay --help")]
        public void Help_DescribesSupportedScopeWithoutReadingFiles(string arguments)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, Program.Run(arguments.Split(' '), output, error));
            Assert.Contains("run --scenario", output.ToString());
            Assert.Contains("replay --input", output.ToString());
            Assert.Contains("--replay-out", output.ToString());
            Assert.Equal("", error.ToString());
        }

        internal static string[] RunArgs(string commands) => new[] { "run", "--scenario", Fixtures.DemoPath, "--seed", "42", "--commands", commands };
        internal static string WriteCommands(SnapshotDirectory directory, byte[] bytes)
        {
            string path = Path.Combine(directory.Path, "commands.json");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal static (int Code, JsonObject Result, string Errors) Invoke(string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = Program.Run(args, output, error);
            Assert.EndsWith("\n", output.ToString());
            return (code, JsonNode.Parse(output.ToString())!.AsObject(), error.ToString());
        }

        private sealed class BrokenWriter : StringWriter
        {
            public override void Write(string? value) => throw new IOException("Broken stream.");
            public override void WriteLine(string? value) => throw new IOException("Broken stream.");
        }
    }
}
