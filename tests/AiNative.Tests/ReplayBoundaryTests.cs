using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class ReplayBoundaryTests
    {
        [Fact]
        public void EmptyRecording_PreservesRandomExtreme_AndComparesOnlyInitialPoint()
        {
            using var directory = new SnapshotDirectory();
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            var run = CliTests.Invoke(new[] { "run", "--scenario", Fixtures.DemoPath, "--seed", "18446744073709551615",
                "--commands", commands, "--replay-out", directory.FilePath });
            Assert.Equal(0, run.Code);
            var replay = CliTests.Invoke(new[] { "replay", "--input", directory.FilePath });
            Assert.Equal(0, replay.Code);
            Assert.Equal(1, replay.Result["comparedCheckpoints"]!.GetValue<int>());
            Assert.Empty(replay.Result["checkpoints"]!.AsArray());
            Assert.True(JsonNode.DeepEquals(run.Result["finalCheckpoint"], replay.Result["finalCheckpoint"]));
        }

        [Theory]
        [InlineData("index", "$.initialCheckpoint.operationIndex")]
        [InlineData("session", "$.checkpoints[5].session")]
        public void CheckpointFieldPriority_PrecedesStateHash(string mutation, string expectedPath)
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = ReplayTests.Fixture();
            JsonNode point = mutation == "index" ? record["initialCheckpoint"]! : record["checkpoints"]![5]!;
            point["stateHash"] = new string('0', 64);
            if (mutation == "index")
            {
                point["operationIndex"] = 0;
            }
            else
            {
                point["session"]!["completedStepCount"] = "0";
            }
            var call = ReplayTests.Replay(directory, record);
            Assert.Equal(2, call.Code);
            Assert.Equal(expectedPath, call.Result["location"]!["path"]!.GetValue<string>());
        }

        [Fact]
        public void SubmitStateContinuity_PrecedesResultKind()
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = ReplayTests.Fixture();
            JsonNode point = record["checkpoints"]![3]!;
            point["state"] = record["checkpoints"]![5]!["state"]!.DeepClone();
            point["stateHash"] = record["checkpoints"]![5]!["stateHash"]!.DeepClone();
            point["result"] = JsonNode.Parse("{\"kind\":\"empty\"}");
            var call = ReplayTests.Replay(directory, record);
            Assert.Equal(2, call.Code);
            Assert.Equal("$.checkpoints[3].state", call.Result["location"]!["path"]!.GetValue<string>());
        }

        [Fact]
        public void UnbalancedButInternallyConsistentReplay_UsesNestedCommandPath()
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = ReplayTests.Fixture();
            record["commandFile"]!["operations"]!.AsArray().Add(JsonNode.Parse("{\"op\":\"submit\",\"command\":{\"type\":\"EndTurn\",\"side\":\"A\"}}"));
            JsonNode point = record["checkpoints"]![14]!.DeepClone();
            point["operationIndex"] = 15;
            point["session"]!["lastSubmittedSequence"] = "8";
            point["session"]!["pendingCount"] = 1;
            point["result"] = JsonNode.Parse("{\"kind\":\"submitted\",\"sequence\":\"8\"}");
            record["checkpoints"]!.AsArray().Add(point);
            var call = ReplayTests.Replay(directory, record);
            Assert.Equal(2, call.Code);
            Assert.Equal("PendingCommands", call.Result["code"]!.GetValue<string>());
            Assert.Equal("$.commandFile.operations", call.Result["location"]!["path"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
        }

        [Theory]
        [InlineData("version", "UnsupportedVersion")]
        [InlineData("environment", "EnvironmentMismatch")]
        [InlineData("config", "ConfigHashMismatch")]
        [InlineData("snapshot", "InvalidSnapshot")]
        [InlineData("checkpoint", "InvalidReplay")]
        public void CompatibilityAndContentErrors_HaveDistinctCodes(string mutation, string code)
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = ReplayTests.Fixture();
            switch (mutation)
            {
                case "version":
                    record["replayFormatVersion"] = 2;
                    break;
                case "environment":
                    record["initialSnapshot"]!["environment"]!["runtimeVersion"] = "0.0.0";
                    break;
                case "config":
                    record["initialSnapshot"]!["configHash"] = new string('0', 64);
                    break;
                case "snapshot":
                    record["initialSnapshot"]!["stateHash"] = new string('0', 64);
                    break;
                case "checkpoint":
                    record["checkpoints"]![14]!["stateHash"] = new string('0', 64);
                    break;
            }
            var call = ReplayTests.Replay(directory, record);
            Assert.Equal(2, call.Code);
            Assert.Equal(code, call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
        }

        [Fact]
        public void AllStructuresPrecedeVersions_AndEnvironmentPrecedesCheckpointSemantics()
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = ReplayTests.Fixture();
            record["replayFormatVersion"] = 2;
            record["checkpoints"]![14]!["unexpected"] = true;
            var structure = ReplayTests.Replay(directory, record);
            Assert.Equal("InvalidReplay", structure.Result["code"]!.GetValue<string>());
            record = ReplayTests.Fixture();
            record["initialSnapshot"]!["environment"]!["os"] = "other";
            record["checkpoints"]![14]!["stateHash"] = new string('0', 64);
            var environment = ReplayTests.Replay(directory, record);
            Assert.Equal("EnvironmentMismatch", environment.Result["code"]!.GetValue<string>());
        }

        [Fact]
        public void ExpectedConfiguration_IsReadAndValidatedWithoutReplacingEmbeddedConfiguration()
        {
            using var directory = new SnapshotDirectory();
            File.WriteAllText(directory.FilePath, ReplayTests.Fixture().ToJsonString());
            JsonNode config = JsonNode.Parse(SnapshotFixtures.Read("scenario.json"))!;
            config["attackDamage"] = 3;
            string path = Path.Combine(directory.Path, "expected.json");
            File.WriteAllText(path, config.ToJsonString());
            var call = CliTests.Invoke(new[] { "replay", "--input", directory.FilePath, "--expect-config", path });
            Assert.Equal(2, call.Code);
            Assert.Equal("ExpectedConfigMismatch", call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
        }

        [Fact]
        public void OutputConflictOrExistingReplay_IsRejectedBeforeExecution()
        {
            using var directory = new SnapshotDirectory();
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            string equivalent = Path.Combine(directory.Path, ".", "state.json");
            var conflict = CliTests.Invoke(CliTests.RunArgs(commands).Concat(new[] { "--snapshot-out", directory.FilePath, "--replay-out", equivalent }).ToArray());
            Assert.Equal(1, conflict.Code);
            Assert.Equal("OutputPathConflict", conflict.Result["code"]!.GetValue<string>());
            Assert.Null(conflict.Result["initialCheckpoint"]);
            File.WriteAllText(directory.FilePath, "keep");
            var exists = CliTests.Invoke(CliTests.RunArgs(commands).Concat(new[] { "--replay-out", directory.FilePath }).ToArray());
            Assert.Equal(1, exists.Code);
            Assert.Equal("OutputExists", exists.Result["code"]!.GetValue<string>());
            Assert.Equal("keep", File.ReadAllText(directory.FilePath));
        }

        [Fact]
        public void ReplayPublishFailure_PreservesEarlierSnapshotAndReportsOnlyPublishedArtifact()
        {
            using var directory = new SnapshotDirectory();
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            // 超长文件名在发布时失败；同目录临时文件仍使用正常长度。
            string replay = Path.Combine(directory.Path, new string('r', 300) + ".json");
            var call = CliTests.Invoke(CliTests.RunArgs(commands).Concat(new[] { "--snapshot-out", directory.FilePath, "--replay-out", replay }).ToArray());
            Assert.Equal(1, call.Code);
            Assert.Equal("OutputWriteFailed", call.Result["code"]!.GetValue<string>());
            Assert.True(File.Exists(directory.FilePath));
            Assert.Equal("snapshot", Assert.Single(call.Result["artifacts"]!.AsArray())!["kind"]!.GetValue<string>());
            Assert.Empty(Directory.GetFiles(directory.Path, ".ainative-*.tmp"));
        }

        [Fact]
        public void StdoutFailure_DoesNotRollbackSnapshotOrReplay()
        {
            using var directory = new SnapshotDirectory();
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            string replay = Path.Combine(directory.Path, "recorded.json");
            var error = new StringWriter();
            int code = Program.Run(CliTests.RunArgs(commands).Concat(new[] { "--snapshot-out", directory.FilePath, "--replay-out", replay }).ToArray(), new BrokenWriter(), error);
            Assert.Equal(1, code);
            Assert.Contains("StandardOutputFailed", error.ToString());
            Assert.True(File.Exists(directory.FilePath));
            Assert.Equal(0, CliTests.Invoke(new[] { "replay", "--input", replay }).Code);
        }

        [Fact]
        public void ValidReplayWhoseRuleFaults_ReportsSimulationFailureWithoutCountingFaultyStep()
        {
            using var directory = new SnapshotDirectory();
            ScenarioDefinition config = Fixtures.Definition();
            GridGameRules rules = GridGameRules.Restore(new GridGameState(config, config.Units, Side.A, 2, long.MaxValue, null));
            var session = SimulationSession<GridCommand, GridGameView>.Restore(rules,
                new SessionState(long.MaxValue - 1, long.MaxValue - 1, DeterministicRandom.Algorithm, 42));
            new SnapshotStore().Save(directory.FilePath, session, rules);
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            string recordPath = Path.Combine(directory.Path, "recorded.json");
            Assert.Equal(0, CliTests.Invoke(new[] { "run", "--snapshot", directory.FilePath, "--commands", commands, "--replay-out", recordPath }).Code);
            JsonObject record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
            record["commandFile"]!["operations"] = JsonNode.Parse("[{\"op\":\"submit\",\"command\":{\"type\":\"EndTurn\",\"side\":\"A\"}},{\"op\":\"advance\"}]");
            JsonNode submitted = record["initialCheckpoint"]!.DeepClone();
            submitted["operationIndex"] = 0;
            submitted["session"]!["lastSubmittedSequence"] = long.MaxValue.ToString();
            submitted["session"]!["pendingCount"] = 1;
            submitted["result"] = JsonNode.Parse("{\"kind\":\"submitted\",\"sequence\":\"9223372036854775807\"}");
            JsonNode executed = submitted.DeepClone();
            executed["operationIndex"] = 1;
            executed["session"]!["completedStepCount"] = long.MaxValue.ToString();
            executed["session"]!["pendingCount"] = 0;
            executed["result"] = JsonNode.Parse("{\"kind\":\"executed\",\"sequence\":\"9223372036854775807\",\"outcome\":\"Accepted\",\"events\":[]}");
            record["checkpoints"] = new JsonArray(submitted, executed);
            var call = ReplayTests.Replay(directory, record);
            Assert.Equal(1, call.Code);
            Assert.Equal("SessionFaulted", call.Result["code"]!.GetValue<string>());
            Assert.Equal(2, call.Result["comparedCheckpoints"]!.GetValue<int>());
            Assert.Single(call.Result["checkpoints"]!.AsArray());
            Assert.Equal(0, call.Result["accepted"]!.GetValue<int>());
            Assert.Null(call.Result["divergence"]);
            Assert.Equal("submitted", call.Result["finalCheckpoint"]!["result"]!["kind"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("{", "InvalidJson")]
        [InlineData("{\"replayFormatVersion\":1,\"replayFormatVersion\":1}", "InvalidJson")]
        [InlineData("null", "InvalidProtocol")]
        [InlineData("[]", "InvalidProtocol")]
        [InlineData("{}", "InvalidProtocol")]
        public void InvalidReplayEnvelope_IsClassifiedBeforeExecution(string json, string code)
        {
            using var directory = new SnapshotDirectory();
            File.WriteAllText(directory.FilePath, json);
            var call = CliTests.Invoke(new[] { "replay", "--input", directory.FilePath });
            Assert.Equal(2, call.Code);
            Assert.Equal(code, call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
        }

        [Fact]
        public void DifferenceIncludesSessionCounters_EvenWhenStateHashMatches()
        {
            CliCheckpoint initial = ReplayReader.Read(SnapshotFixtures.Read("replay.json")).Initial.Value;
            CliCheckpoint changed = initial with { Submitted = 1, Pending = 1 };
            ReplayDivergence difference = Assert.IsType<ReplayDivergence>(CheckpointDifference.Compare(initial, changed));
            Assert.Equal(difference.ExpectedHash, difference.ActualHash);
            Assert.Equal(new[] { "session.lastSubmittedSequence", "session.pendingCount" }, difference.Differences.Select(item => item.Path));
            Assert.Equal("0", difference.Differences[0].Expected!.Value.GetString());
            Assert.Equal("1", difference.Differences[0].Actual!.Value.GetString());
        }

        private sealed class BrokenWriter : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(string? value) => throw new IOException("broken stdout");
        }
    }
}
