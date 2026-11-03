using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class ReplayTests
    {
        [Fact]
        public void Recording_MatchesFixedReplay_AndVerificationReportsActualCheckpoints()
        {
            using var directory = new SnapshotDirectory();
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("commands.json"));
            string replayPath = Path.Combine(directory.Path, "recorded.json");
            var run = CliTests.Invoke(new[] { "run", "--scenario", Fixtures.DemoPath, "--seed", "42", "--commands", commands,
                "--snapshot-out", directory.FilePath, "--replay-out", replayPath });
            Assert.Equal(0, run.Code);
            Assert.Equal(new[] { "snapshot", "replay" }, run.Result["artifacts"]!.AsArray().Select(item => item!["kind"]!.GetValue<string>()));
            JsonObject fixedReplay = Fixture();
            Assert.True(JsonNode.DeepEquals(fixedReplay, JsonNode.Parse(File.ReadAllBytes(replayPath))));
            File.Delete(commands);
            var replay = CliTests.Invoke(new[] { "replay", "--input", replayPath, "--expect-config", Fixtures.DemoPath });
            Assert.Equal(0, replay.Code);
            Assert.Equal("ReplayMatched", replay.Result["code"]!.GetValue<string>());
            Assert.Equal(16, replay.Result["comparedCheckpoints"]!.GetValue<int>());
            foreach (string field in new[] { "accepted", "rejected", "initialCheckpoint", "checkpoints", "finalCheckpoint" })
            {
                Assert.True(JsonNode.DeepEquals(run.Result[field], replay.Result[field]), field);
            }
            Assert.Null(replay.Result["divergence"]);
            Assert.Empty(replay.Result["artifacts"]!.AsArray());
        }

        [Fact]
        public void EventDeviation_MatchesFixedDivergenceResponse_AndStopsAtFirstMismatch()
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = Fixture("event-divergence.replay.json");
            var call = Replay(directory, record);
            Assert.Equal(3, call.Code);
            JsonObject expected = JsonNode.Parse(SnapshotFixtures.Read("divergence-result.json"))!.AsObject();
            foreach (var property in expected.Where(property => property.Key != "message"))
            {
                Assert.True(JsonNode.DeepEquals(property.Value, call.Result[property.Key]), property.Key);
            }
        }

        [Fact]
        public void ChangedCommand_KeepsExistingExpectations_AndReportsStateAndEventDifferences()
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = Fixture();
            record["commandFile"]!["operations"]![3]!["command"]!["direction"] = "Up";
            var call = Replay(directory, record);
            Assert.Equal(3, call.Code);
            Assert.Equal(5, call.Result["location"]!["operationIndex"]!.GetValue<int>());
            Assert.Equal("2", call.Result["location"]!["sequence"]!.GetValue<string>());
            Assert.Equal(6, call.Result["comparedCheckpoints"]!.GetValue<int>());
            Assert.Equal(6, call.Result["checkpoints"]!.AsArray().Count);
            string[] paths = call.Result["divergence"]!["differences"]!.AsArray().Select(item => item!["path"]!.GetValue<string>()).ToArray();
            Assert.Contains("state.game.units[id=1].position.x", paths);
            Assert.Contains("result.events[0].to.y", paths);
            Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);
            Assert.NotEqual(call.Result["divergence"]!["expectedHash"]!.GetValue<string>(), call.Result["divergence"]!["actualHash"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(call.Result["checkpoints"]![5], call.Result["finalCheckpoint"]));
        }

        [Theory]
        [InlineData("health", "state.game.units[id=2].health")]
        [InlineData("random", "state.random.state")]
        [InlineData("rejection", "result.errorCode")]
        [InlineData("missingEvent", "result.events[0]")]
        [InlineData("extraEvent", "result.events[1]")]
        public void ValidButIncorrectExpectations_AreDivergences(string mutation, string expectedPath)
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = Fixture();
            JsonNode checkpoint = record["checkpoints"]![5]!;
            if (mutation == "health")
            {
                checkpoint["state"]!["game"]!["units"]![1]!["health"] = 3;
                Rehash(checkpoint);
            }
            else if (mutation == "random")
            {
                checkpoint["state"]!["random"]!["state"] = "43";
                Rehash(checkpoint);
            }
            else if (mutation == "rejection")
            {
                record["checkpoints"]![2]!["result"]!["errorCode"] = "UnknownTarget";
            }
            else if (mutation == "missingEvent")
            {
                checkpoint["result"]!["events"]!.AsArray().Clear();
            }
            else
            {
                checkpoint["result"]!["events"]!.AsArray().Add(JsonNode.Parse("{\"type\":\"GameEnded\",\"winner\":\"A\"}"));
            }
            var call = Replay(directory, record);
            Assert.Equal(3, call.Code);
            JsonNode difference = Assert.Single(call.Result["divergence"]!["differences"]!.AsArray())!;
            Assert.Equal(expectedPath, difference["path"]!.GetValue<string>());
            if (mutation == "missingEvent" || mutation == "extraEvent")
            {
                Assert.Equal(mutation == "missingEvent" ? "Added" : "Missing", difference["kind"]!.GetValue<string>());
            }
        }

        [Theory]
        [InlineData("missingCheckpoint")]
        [InlineData("extraCheckpoint")]
        [InlineData("index")]
        [InlineData("session")]
        [InlineData("pending")]
        [InlineData("sequence")]
        [InlineData("kind")]
        [InlineData("hash")]
        [InlineData("health")]
        [InlineData("configHash")]
        [InlineData("digest")]
        [InlineData("algorithm")]
        [InlineData("initialState")]
        [InlineData("initialKind")]
        [InlineData("submitState")]
        [InlineData("emptyState")]
        [InlineData("null")]
        [InlineData("unknownField")]
        [InlineData("unknownEvent")]
        [InlineData("negativeDamage")]
        [InlineData("rejectedEvents")]
        [InlineData("unknownError")]
        public void DamagedRecord_IsRejectedBeforeAnyExecution(string mutation)
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = Fixture();
            JsonArray points = record["checkpoints"]!.AsArray();
            JsonNode checkpoint = points[5]!;
            switch (mutation)
            {
                case "missingCheckpoint":
                    points.RemoveAt(points.Count - 1);
                    break;
                case "extraCheckpoint":
                    points.Add(points[0]!.DeepClone());
                    break;
                case "index":
                    checkpoint["operationIndex"] = 4;
                    break;
                case "session":
                    checkpoint["session"]!["completedStepCount"] = "1";
                    break;
                case "pending":
                    checkpoint["session"]!["pendingCount"] = -1;
                    break;
                case "sequence":
                    checkpoint["result"]!["sequence"] = "1";
                    break;
                case "kind":
                    checkpoint["result"] = JsonNode.Parse("{\"kind\":\"empty\"}");
                    break;
                case "hash":
                    checkpoint["stateHash"] = new string('0', 64);
                    break;
                case "health":
                    checkpoint["state"]!["game"]!["units"]![0]!["health"] = 999;
                    Rehash(checkpoint);
                    break;
                case "configHash":
                    checkpoint["state"]!["configHash"] = new string('0', 64);
                    break;
                case "digest":
                    checkpoint["state"]!["digestVersion"] = 2;
                    break;
                case "algorithm":
                    checkpoint["state"]!["random"]!["algorithm"] = "other";
                    break;
                case "initialState":
                    record["initialCheckpoint"]!["state"]!["random"]!["state"] = "43";
                    Rehash(record["initialCheckpoint"]!);
                    break;
                case "initialKind":
                    record["initialCheckpoint"]!["result"]!["kind"] = "empty";
                    break;
                case "submitState":
                    points[3]!["state"]!["random"]!["state"] = "43";
                    Rehash(points[3]!);
                    break;
                case "emptyState":
                    points[0]!["state"]!["random"]!["state"] = "43";
                    Rehash(points[0]!);
                    break;
                case "null":
                    checkpoint["result"] = null;
                    break;
                case "unknownField":
                    checkpoint["result"]!["extra"] = 1;
                    break;
                case "unknownEvent":
                    checkpoint["result"]!["events"]![0]!["type"] = "Unknown";
                    break;
                case "negativeDamage":
                    points[6]!["result"]!["events"]![0]!["damage"] = -1;
                    break;
                case "rejectedEvents":
                    points[2]!["result"]!["events"]!.AsArray().Add(checkpoint["result"]!["events"]![0]!.DeepClone());
                    break;
                case "unknownError":
                    points[2]!["result"]!["errorCode"] = "Anything";
                    break;
            }
            var call = Replay(directory, record);
            Assert.Equal(2, call.Code);
            Assert.Equal("InvalidReplay", call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
            Assert.Empty(call.Result["checkpoints"]!.AsArray());
            Assert.Equal(0, call.Result["comparedCheckpoints"]!.GetValue<int>());
        }

        [Fact]
        public void PermutedUnits_StillMatchSemanticState()
        {
            using var directory = new SnapshotDirectory();
            JsonObject record = Fixture();
            foreach (JsonNode point in record["checkpoints"]!.AsArray().Select(point => point!).Prepend(record["initialCheckpoint"]!))
            {
                JsonArray units = point["state"]!["game"]!["units"]!.AsArray();
                JsonNode first = units[0]!.DeepClone();
                units.RemoveAt(0);
                units.Add(first);
            }
            var call = Replay(directory, record);
            Assert.Equal(0, call.Code);
            Assert.Equal(16, call.Result["comparedCheckpoints"]!.GetValue<int>());
        }

        [Theory]
        [InlineData("replay")]
        [InlineData("replay --input a --input b")]
        [InlineData("replay --snapshot a")]
        [InlineData("replay --input a --commands b")]
        [InlineData("replay --input a --snapshot-out b")]
        [InlineData("replay --input a --replay-out b")]
        [InlineData("inspect --snapshot a --replay-out b")]
        public void ReplayArguments_AreStrict(string arguments)
        {
            var call = CliTests.Invoke(arguments.Split(' '));
            Assert.Equal(2, call.Code);
            Assert.Equal("InvalidArguments", call.Result["code"]!.GetValue<string>());
        }

        internal static JsonObject Fixture(string file = "replay.json")
        {
            JsonObject record = JsonNode.Parse(SnapshotFixtures.Read(file))!.AsObject();
            HostEnvironment environment = SnapshotEnvironment.Current();
            JsonNode target = record["initialSnapshot"]!["environment"]!;
            target["runtimeVersion"] = environment.RuntimeVersion;
            target["os"] = environment.Os;
            target["architecture"] = environment.Architecture;
            target["buildHash"] = environment.BuildHash;
            return record;
        }

        internal static (int Code, JsonObject Result, string Errors) Replay(SnapshotDirectory directory, JsonObject record)
        {
            File.WriteAllText(directory.FilePath, record.ToJsonString());
            return CliTests.Invoke(new[] { "replay", "--input", directory.FilePath });
        }

        private static void Rehash(JsonNode checkpoint)
        {
            // 夹具字段本来就是固定规范顺序；只更新刻意构造的合法变体，不生成正确执行期望。
            checkpoint["stateHash"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint["state"]!.ToJsonString())));
        }
    }
}
