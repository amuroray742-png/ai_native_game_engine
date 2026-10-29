using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CliProcessTests
    {
        [Theory]
        [InlineData(7, "4")]
        [InlineData(9, "5")]
        public async Task Run_Save_Resume_AndInspect_WorkAcrossProcessesAndWorkingDirectories(int split, string nextSequence)
        {
            using var directory = new SnapshotDirectory();
            File.WriteAllBytes(Path.Combine(directory.Path, "scenario.json"), SnapshotFixtures.Read("scenario.json"));
            JsonObject commands = JsonNode.Parse(SnapshotFixtures.Read("commands.json"))!.AsObject();
            JsonArray operations = commands["operations"]!.AsArray();
            File.WriteAllText(Path.Combine(directory.Path, "all.json"), commands.ToJsonString());
            File.WriteAllText(Path.Combine(directory.Path, "first.json"), Commands(operations.Take(split)).ToJsonString());
            File.WriteAllText(Path.Combine(directory.Path, "rest.json"), Commands(operations.Skip(split)).ToJsonString());
            var continuous = await Start(directory.Path, "run", "--scenario", "scenario.json", "--seed", "42", "--commands", "all.json");
            var first = await Start(directory.Path, "run", "--scenario", "scenario.json", "--seed", "42", "--commands", "first.json", "--snapshot-out", "mid.json");
            Assert.Equal(0, continuous.Code);
            Assert.Equal(0, first.Code);
            File.Delete(Path.Combine(directory.Path, "scenario.json"));
            var resumed = await Start(directory.Path, "run", "--snapshot", "mid.json", "--commands", "rest.json", "--snapshot-out", "end.json");
            Assert.Equal(0, resumed.Code);
            var inspected = await Start(directory.Path, "inspect", "--snapshot", "end.json");
            Assert.Equal(0, inspected.Code);
            JsonNode whole = JsonNode.Parse(continuous.Output)!;
            JsonNode continuation = JsonNode.Parse(resumed.Output)!;
            foreach (string field in new[] { "state", "stateHash", "session", "result" })
            {
                Assert.True(JsonNode.DeepEquals(whole["finalCheckpoint"]![field], continuation["finalCheckpoint"]![field]), field);
            }
            Assert.Equal(nextSequence, continuation["checkpoints"]![0]!["result"]!["sequence"]!.GetValue<string>());
            JsonNode inspect = JsonNode.Parse(inspected.Output)!;
            Assert.True(JsonNode.DeepEquals(continuation["finalCheckpoint"]!["state"], inspect["initialCheckpoint"]!["state"]));
            string published = continuation["artifacts"]![0]!["path"]!.GetValue<string>();
            Assert.True(Path.IsPathFullyQualified(published));
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory.Path, "end.json")), File.ReadAllBytes(published));
            Assert.Equal("", continuous.Error + first.Error + resumed.Error + inspected.Error);
        }

        [Fact]
        public async Task ReservedAndOptionLikeFileNames_AreExplicitValuesOrLegacyPaths()
        {
            using var directory = new SnapshotDirectory();
            File.WriteAllBytes(Path.Combine(directory.Path, "run"), SnapshotFixtures.Read("scenario.json"));
            File.WriteAllBytes(Path.Combine(directory.Path, "--commands"), SnapshotFixtures.Read("scenario.json"));
            File.WriteAllBytes(Path.Combine(directory.Path, "--seed"), SnapshotFixtures.Read("empty-commands.json"));
            File.WriteAllBytes(Path.Combine(directory.Path, "--legacy.json"), SnapshotFixtures.Read("scenario.json"));
            var optionLikeLegacy = await Start(directory.Path, "--legacy.json");
            Assert.Equal(0, optionLikeLegacy.Code);
            Assert.Contains("Demo passed", optionLikeLegacy.Output);
            var legacy = await Start(directory.Path, "./run");
            Assert.Equal(0, legacy.Code);
            Assert.Contains("Demo passed", legacy.Output);
            var batch = await Start(directory.Path, "run", "--scenario", "--commands", "--seed", "42", "--commands", "--seed");
            Assert.Equal(0, batch.Code);
            Assert.Equal("RunCompleted", JsonNode.Parse(batch.Output)!["code"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("missing", "InputReadFailed")]
        [InlineData("malformed", "InvalidJson")]
        [InlineData("fixture", "EnvironmentMismatch")]
        public async Task Inspect_ReportsStructuredInputErrorsWithExitTwo(string variant, string expectedCode)
        {
            using var directory = new SnapshotDirectory();
            if (variant == "malformed")
            {
                File.WriteAllText(directory.FilePath, "{");
            }
            else if (variant == "fixture")
            {
                File.WriteAllBytes(directory.FilePath, SnapshotFixtures.Read("initial.snapshot.json"));
            }
            var result = await Start(directory.Path, "inspect", "--snapshot", "state.json");
            Assert.Equal(2, result.Code);
            Assert.Equal(expectedCode, JsonNode.Parse(result.Output)!["code"]!.GetValue<string>());
            Assert.Contains(expectedCode, result.Error);
            if (variant == "missing")
            {
                Assert.True(Path.IsPathFullyQualified(JsonNode.Parse(result.Output)!["location"]!["path"]!.GetValue<string>()));
            }
        }

        private static JsonObject Commands(System.Collections.Generic.IEnumerable<JsonNode?> operations) =>
            new JsonObject { ["commandFormatVersion"] = 1, ["operations"] = new JsonArray(operations.Select(item => item?.DeepClone()).ToArray()) };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RecordedReplay_IsSelfContainedAcrossProcesses_FromNewOrMidGame(bool fromMidGame)
        {
            using var directory = new SnapshotDirectory();
            File.WriteAllBytes(Path.Combine(directory.Path, "scenario.json"), SnapshotFixtures.Read("scenario.json"));
            JsonNode file = JsonNode.Parse(SnapshotFixtures.Read("commands.json"))!;
            JsonArray operations = file["operations"]!.AsArray();
            File.WriteAllText(Path.Combine(directory.Path, "first.json"), Commands(operations.Take(7)).ToJsonString());
            File.WriteAllText(Path.Combine(directory.Path, "rest.json"), Commands(operations.Skip(7)).ToJsonString());
            File.WriteAllText(Path.Combine(directory.Path, "all.json"), file.ToJsonString());
            if (fromMidGame)
            {
                var prefix = await Start(directory.Path, "run", "--scenario", "scenario.json", "--seed", "42", "--commands", "first.json", "--snapshot-out", "mid.json");
                Assert.Equal(0, prefix.Code);
            }
            string[] arguments = fromMidGame
                ? new[] { "run", "--snapshot", "mid.json", "--commands", "rest.json", "--replay-out", "recorded.json" }
                : new[] { "run", "--scenario", "scenario.json", "--seed", "42", "--commands", "all.json", "--replay-out", "recorded.json" };
            var run = await Start(directory.Path, arguments);
            Assert.Equal(0, run.Code);
            foreach (string input in new[] { "scenario.json", "first.json", "rest.json", "all.json", "mid.json" })
            {
                File.Delete(Path.Combine(directory.Path, input));
            }
            var replay = await Start(directory.Path, "replay", "--input", "recorded.json");
            Assert.Equal(0, replay.Code);
            Assert.Equal("", replay.Error);
            JsonNode expected = JsonNode.Parse(run.Output)!;
            JsonNode actual = JsonNode.Parse(replay.Output)!;
            Assert.True(JsonNode.DeepEquals(expected["checkpoints"], actual["checkpoints"]));
            Assert.True(JsonNode.DeepEquals(expected["finalCheckpoint"], actual["finalCheckpoint"]));
            Assert.Equal(fromMidGame ? 9 : 16, actual["comparedCheckpoints"]!.GetValue<int>());
            JsonObject changed = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, "recorded.json")))!.AsObject();
            int eventIndex = fromMidGame ? 1 : 5;
            changed["checkpoints"]![eventIndex]!["result"]!["events"]!.AsArray().Clear();
            File.WriteAllText(Path.Combine(directory.Path, "recorded.json"), changed.ToJsonString());
            var divergence = await Start(directory.Path, "replay", "--input", "recorded.json");
            Assert.Equal(3, divergence.Code);
            Assert.Equal("CheckpointMismatch", JsonNode.Parse(divergence.Output)!["code"]!.GetValue<string>());
            Assert.Contains("CheckpointMismatch", divergence.Error);
        }

        private static async Task<(int Code, string Output, string Error)> Start(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start test process.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await output, await error);
        }
    }
}
