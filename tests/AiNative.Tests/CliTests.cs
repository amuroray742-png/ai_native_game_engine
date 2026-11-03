using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AiNative.Core;
using AiNative.Headless;
using AiNative.Hosting;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CliTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("--help")]
        [InlineData("run --help")]
        [InlineData("compare --help")]
        public void HelpDescribesV2WithoutReadingFilesOrEnvironment(string arguments)
        {
            using var output = new StringWriter();
            string[] args = arguments.Length == 0 ? Array.Empty<string>() : arguments.Split(' ');
            Assert.Equal(0, new CliRunner(Fixtures.Registry(), _ => throw new Exception("Must not probe")).Run(args, output, TextWriter.Null));
            Assert.Contains("games", output.ToString());
            Assert.Contains("compare", output.ToString());
            Assert.Contains("--snapshot", output.ToString());
            Assert.Contains("v2", output.ToString());
        }

        [Theory]
        [InlineData("--help")]
        [InlineData("run")]
        [InlineData("--seed")]
        public void ReservedFileNamesRemainExplicitOptionValues(string commandsName)
        {
            using var directory = new TestDirectory();
            directory.Write("games", Fixtures.Read("grid.scenario.json"));
            directory.Write(commandsName, Fixtures.Read("empty.commands.json"));
            string previous = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(directory.Path);
                Assert.Equal(0, Fixtures.Invoke("run", "--scenario", "games", "--seed", "42", "--commands", commandsName).Code);
            }
            finally { Directory.SetCurrentDirectory(previous); }
        }

        [Fact]
        public void SecondPublishFailureReportsOnlyTheAlreadyPublishedSnapshot()
        {
            using var directory = new TestDirectory();
            string snapshot = directory.File("saved.json"), replay = directory.File("raced.json");
            var module = new PublishingRaceModule(Fixtures.Registry().Get("grid-game"), () => File.WriteAllText(replay, "racer"));
            using var output = new StringWriter();
            int exit = new CliRunner(new GameRegistry(module), _ => Fixtures.Environment()).Run(new[] { "run",
                "--scenario", Fixtures.Data("grid.scenario.json"), "--seed", "42", "--commands", Fixtures.Data("grid.commands.json"),
                "--snapshot-out", snapshot, "--replay-out", replay }, output, TextWriter.Null);
            JsonElement response = Fixtures.Json(output.ToString());
            Assert.Equal(1, exit);
            Assert.Equal("OutputExists", response.GetProperty("code").GetString());
            Assert.Equal(snapshot, Assert.Single(response.GetProperty("artifacts").EnumerateArray()).GetString());
            Assert.True(File.Exists(snapshot));
            Assert.Equal("racer", File.ReadAllText(replay));
            Assert.Equal(6, response.GetProperty("accepted").GetInt32());
            Assert.Equal(0, Fixtures.Invoke("inspect", "--snapshot", snapshot).Code);
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("run")]
        [InlineData("inspect")]
        [InlineData("replay")]
        [InlineData("compare")]
        [InlineData("describe")]
        [InlineData("--help junk")]
        [InlineData("games --seed 1")]
        [InlineData("run --scenario a --seed 01 --commands b")]
        [InlineData("run --scenario a --seed -1 --commands b")]
        [InlineData("run --scenario a --seed 18446744073709551616 --commands b")]
        [InlineData("run --snapshot a --seed 1 --commands b")]
        [InlineData("run --scenario a --snapshot b --seed 1 --commands c")]
        [InlineData("run --scenario a --seed 1 --commands b --commands c")]
        [InlineData("run --scenario a --seed 1 --commands b --expect-config c")]
        [InlineData("compare --expected a --actual b --extra c")]
        public void InvalidArgumentsFailBeforeInputReads(string arguments)
        {
            var result = Fixtures.Invoke(arguments.Split(' '));
            Assert.Equal(2, result.Code);
            Assert.Equal("InvalidArguments", result.Result.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, result.Result.GetProperty("initialCheckpoint").ValueKind);
        }

        [Theory]
        [InlineData("games")]
        [InlineData("describe --game grid-game")]
        [InlineData("describe --game physics-sandbox")]
        public void DiscoveryDoesNotProbeEnvironmentOrCreateWorld(string arguments)
        {
            using var output = new StringWriter();
            Assert.Equal(0, new CliRunner(Fixtures.Registry(), _ => throw new Exception("Must not probe")).Run(arguments.Split(' '), output, TextWriter.Null));
            Assert.Equal("Completed", Fixtures.Json(output.ToString()).GetProperty("status").GetString());
        }

        [Fact]
        public void FixedRunReportsDocumentedOutcomeAndPreservesPublishedArtifactsOnStdoutFailure()
        {
            using var directory = new TestDirectory();
            string snapshot = directory.File("snapshot.json"), replay = directory.File("replay.json");
            string[] args = { "run", "--scenario", Fixtures.Data("grid.scenario.json"), "--seed", "42", "--commands", Fixtures.Data("grid.commands.json"),
                "--snapshot-out", snapshot, "--replay-out", replay };
            Assert.Equal(1, new CliRunner(Fixtures.Registry(), _ => Fixtures.Environment()).Run(args, new BrokenWriter(), new BrokenWriter()));
            Assert.True(File.Exists(snapshot));
            Assert.True(File.Exists(replay));
            var inspected = Fixtures.Invoke("inspect", "--snapshot", snapshot);
            Assert.Equal(0, inspected.Code);
            Assert.Equal(0, inspected.Result.GetProperty("accepted").GetInt32());
            Assert.Equal("A", inspected.Result.GetProperty("observation").GetProperty("winner").GetString());
            Assert.Equal(0, Fixtures.Invoke("replay", "--input", replay).Code);
        }

        [Fact]
        public void PreflightAndExpectedConfigurationFailWithoutExecution()
        {
            using var directory = new TestDirectory();
            string target = directory.Write("existing.json", JsonData.EmptyObject);
            var failed = Fixtures.Invoke("run", "--scenario", Fixtures.Data("grid.scenario.json"), "--seed", "42", "--commands", Fixtures.Data("grid.commands.json"), "--snapshot-out", target);
            Assert.Equal(1, failed.Code);
            Assert.Equal("OutputExists", failed.Result.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, failed.Result.GetProperty("initialCheckpoint").ValueKind);
            string snapshot = directory.Write("saved.json", Fixtures.Record().Snapshot);
            string mismatch = directory.Write("scene.json", HostingTests.Change(Fixtures.Read("grid.scenario.json"), node => node["configuration"]!["width"] = 8));
            Assert.Equal("ExpectedConfigMismatch", Fixtures.Invoke("inspect", "--snapshot", snapshot, "--expect-config", mismatch).Result.GetProperty("code").GetString());
            var collision = Fixtures.Invoke("run", "--snapshot", snapshot, "--commands", Fixtures.Data("empty.commands.json"),
                "--snapshot-out", directory.File("same"), "--replay-out", directory.File("same"));
            Assert.Equal("OutputCollision", collision.Result.GetProperty("code").GetString());
        }

        [Fact]
        public void FaultPreservesOnlyTrustedPrefixAndWritesDiagnostic()
        {
            using var directory = new TestDirectory();
            JsonElement initial = Fixtures.Record(commands: Fixtures.Read("empty.commands.json")).Snapshot;
            JsonElement limit = HostingTests.Change(initial, node =>
            {
                node["runtime"]!["tick"] = long.MaxValue.ToString();
                HostingTests.Rehash(node, initial);
            });
            string snapshot = directory.Write("limit.json", limit);
            string commands = directory.Write("commands.json", Fixtures.Json("{\"formatVersion\":2,\"operations\":[{\"op\":\"submit\",\"command\":{\"type\":\"EndTurn\",\"side\":\"A\"}},{\"op\":\"advance\"}]}"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, new CliRunner(Fixtures.Registry(), _ => Fixtures.Environment()).Run(
                new[] { "run", "--snapshot", snapshot, "--commands", commands }, output, error));
            JsonElement failure = Fixtures.Json(output.ToString());
            Assert.Equal("SimulationFault", failure.GetProperty("code").GetString());
            Assert.Equal(1, failure.GetProperty("location").GetProperty("operationIndex").GetInt32());
            Assert.Single(failure.GetProperty("checkpoints").EnumerateArray());
            Assert.Equal("1", failure.GetProperty("finalCheckpoint").GetProperty("runtime").GetProperty("submitted").GetString());
            Assert.Equal(0, failure.GetProperty("accepted").GetInt32());
            Assert.NotEmpty(error.ToString());
        }

        [Theory]
        [InlineData("existing")]
        [InlineData("partial")]
        [InlineData("race")]
        [InlineData("missing-parent")]
        public void AtomicPublisherNeverOverwritesAndCleansItsTemporaryFile(string variant)
        {
            using var directory = new TestDirectory();
            string target = directory.File("output.json");
            bool wrote = false;
            if (variant == "existing")
            {
                File.WriteAllText(target, "original");
            }
            if (variant == "missing-parent")
            {
                target = directory.File("absent/output.json");
            }
            Assert.Throws<HostOperationException>(() => AtomicFilePublisher.Publish(target, stream =>
            {
                wrote = true;
                stream.WriteByte(1);
                if (variant == "partial")
                {
                    throw new IOException("injected failure");
                }
                if (variant == "race")
                {
                    File.WriteAllText(target, "racer");
                }
            }));
            if (variant == "existing")
            {
                Assert.False(wrote);
                Assert.Equal("original", File.ReadAllText(target));
            }
            if (variant == "race")
            {
                Assert.Equal("racer", File.ReadAllText(target));
            }
            if (variant == "partial" || variant == "missing-parent")
            {
                Assert.False(File.Exists(target));
            }
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        [Fact]
        public void BuildIdentityHashesActualAssembliesAndOptionalNativeFiles()
        {
            HostEnvironment grid = BuildEnvironment.Current(Fixtures.Registry().Get("grid-game"));
            HostEnvironment physics = BuildEnvironment.Current(Fixtures.Registry().Get("physics-sandbox"));
            JsonElement components = grid.ToJson().GetProperty("components");
            Assert.DoesNotContain(components.EnumerateObject(), item => item.Name.Contains("jolt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(physics.ToJson().GetProperty("components").EnumerateObject(), item => item.Name.Contains("jolt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(components.EnumerateObject(), item => item.Name.Contains("Core") && item.Value.GetString()!.Length == 64);
        }

        private sealed class BrokenWriter : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override void WriteLine(string? value) => throw new IOException("closed pipe");
        }

        private sealed class PublishingRaceModule : IGameModule
        {
            private readonly IGameModule _inner;
            private readonly Action _race;
            internal PublishingRaceModule(IGameModule inner, Action race) { _inner = inner; _race = race; }
            public string Id => _inner.Id;
            public int Version => _inner.Version;
            public SimulationTiming Timing => _inner.Timing;
            public JsonElement Description => _inner.Description;
            public IReadOnlyList<Type> CodeTypes => _inner.CodeTypes;
            public IReadOnlyList<string> NativeLibraries => _inner.NativeLibraries;
            public JsonElement ReadConfiguration(JsonElement value) => _inner.ReadConfiguration(value);
            public JsonElement ReadCommand(JsonElement value) => _inner.ReadCommand(value);
            public JsonElement ReadState(JsonElement configuration, JsonElement state, long processed) => _inner.ReadState(configuration, state, processed);
            public void ValidateResult(JsonElement result) => _inner.ValidateResult(result);
            public IHostedGame Create(JsonElement configuration, ulong seed) => new PublishingRaceGame(_inner.Create(configuration, seed), _race);
            public IHostedGame Restore(JsonElement configuration, JsonElement state, RuntimeState runtime) => _inner.Restore(configuration, state, runtime);
        }

        private sealed class PublishingRaceGame : IHostedGame
        {
            private readonly IHostedGame _inner;
            private readonly Action _race;
            internal PublishingRaceGame(IHostedGame inner, Action race) { _inner = inner; _race = race; }
            public long Tick => _inner.Tick;
            public long Submitted => _inner.Submitted;
            public long Processed => _inner.Processed;
            public int Pending => _inner.Pending;
            public ulong RandomState => _inner.RandomState;
            public SimulationTiming Timing => _inner.Timing;
            public long Submit(JsonElement command) => _inner.Submit(command);
            public JsonElement Advance() { _race(); return _inner.Advance(); }
            public JsonElement Observe() => _inner.Observe();
            public JsonElement CaptureGameState() => _inner.CaptureGameState();
            public void Dispose() => _inner.Dispose();
        }
    }
}
