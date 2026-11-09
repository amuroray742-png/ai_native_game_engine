using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using AiNative.Hosting;
using Xunit;

namespace AiNative.Tests
{
    public sealed class HostFailureTests
    {
        [Theory]
        [InlineData("observation")]
        [InlineData("state")]
        [InlineData("publish")]
        public void EncodingAndFrameDeliveryFailuresTerminateTheSession(string fault)
        {
            using var runtime = Fixtures.Session();
            using var other = Fixtures.Session();
            var queue = new SequentialPresentationQueue<GridGameView>(fault == "publish" ? other.Capture() : runtime.Capture());
            using var host = new BrokenEncoder(runtime, queue, fault);
            if (fault == "observation")
            {
                Assert.Throws<InvalidOperationException>(() => host.Observe());
            }
            else if (fault == "state")
            {
                Assert.Throws<InvalidOperationException>(() => host.CaptureGameState());
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => host.AdvanceTick());
            }
            Assert.True(runtime.IsFaulted);
            Assert.Null(queue.BeginNext());
            Assert.Throws<InvalidOperationException>(() => host.AdvanceTick());
            Assert.Throws<InvalidOperationException>(() => host.CaptureGameState());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedTickEncodingStopsBothViewsAndSnapshotCapture(bool typed)
        {
            using var runtime = Fixtures.Session();
            var queue = new SequentialPresentationQueue<GridGameView>(runtime.Capture());
            using var host = new BrokenEncoder(runtime, queue);
            host.Submit(new Move(Side.A, 1, MoveDirection.Right));
            if (typed)
            {
                TickResult<GridGameView> frame = host.AdvanceTick();
                Assert.Throws<InvalidOperationException>(() => host.EncodeTick(frame));
                Assert.Equal(1, queue.PendingCount);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => host.Advance());
                Assert.Equal(0, queue.PendingCount);
            }
            Assert.True(runtime.IsFaulted);
            Assert.Equal(1, runtime.Tick);
            Assert.Throws<InvalidOperationException>(() => host.Submit(new EndTurn(Side.A)));
            Assert.Throws<InvalidOperationException>(() => host.AdvanceTick());
            Assert.Throws<InvalidOperationException>(() => host.Advance());
            Assert.Throws<InvalidOperationException>(() => host.CaptureObservation());
            Assert.Throws<InvalidOperationException>(() => host.CaptureGameState());
            Assert.Throws<InvalidOperationException>(() => runtime.ExportState());
        }

        [Theory]
        [InlineData("tick")]
        [InlineData("submitted")]
        [InlineData("processed")]
        [InlineData("pending")]
        [InlineData("random")]
        [InlineData("timing")]
        [InlineData("state")]
        public void RestoreRejectsChangedCandidateAndDisposesIt(string field)
        {
            var module = new FaultModule(field);
            SnapshotData snapshot = StateCodec.Read(Fixtures.Record().Snapshot, new GameRegistry(module));
            ProtocolException error = Assert.Throws<ProtocolException>(() => StateCodec.Restore(snapshot, Fixtures.Environment()));
            Assert.Equal(field == "state" ? "RestoreStateMismatch" : "RestoreRuntimeMismatch", error.Code);
            Assert.NotNull(module.Candidate);
            Assert.Equal(1, module.Candidate.Disposals);
        }

        [Theory]
        [InlineData("result", 0)]
        [InlineData("json", 0)]
        [InlineData("create", null)]
        [InlineData("tick", null)]
        [InlineData("state", null)]
        public void CliExecutionProtocolFailuresKeepTrustedPrefixAndExitOne(string fault, int? operation)
        {
            using var directory = new TestDirectory();
            string commands = directory.Write("commands.json", Fixtures.Json("""{"formatVersion":2,"operations":[{"op":"advance"}]}"""));
            string snapshot = directory.Write("input.json", Fixtures.Record().Snapshot);
            string outputPath = directory.File("unpublished.json");
            var module = new FaultModule(fault);
            string[] input = fault == "tick" || fault == "state"
                ? new[] { "--snapshot", snapshot }
                : new[] { "--scenario", Fixtures.Data("grid.scenario.json"), "--seed", "42" };
            var args = new List<string> { "run" };
            args.AddRange(input);
            args.AddRange(new[] { "--commands", commands, "--replay-out", outputPath });
            using var output = new StringWriter();
            int exit = new CliRunner(new GameRegistry(module), _ => Fixtures.Environment()).Run(args.ToArray(), output, TextWriter.Null);
            JsonElement result = Fixtures.Json(output.ToString());
            Assert.Equal(1, exit);
            Assert.Equal("SimulationFailure", result.GetProperty("category").GetString());
            JsonElement location = result.GetProperty("location").GetProperty("operationIndex");
            if (operation.HasValue)
            {
                Assert.Equal(operation.Value, location.GetInt32());
                Assert.Equal("0", result.GetProperty("initialCheckpoint").GetProperty("runtime").GetProperty("tick").GetString());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, location.ValueKind);
                Assert.Equal(JsonValueKind.Null, result.GetProperty("initialCheckpoint").ValueKind);
            }
            Assert.Empty(result.GetProperty("checkpoints").EnumerateArray());
            Assert.False(File.Exists(outputPath));
        }

        [Fact]
        public void NegativeZeroReplayIndexIsRejectedBeforeEnvironmentOrExecution()
        {
            using var directory = new TestDirectory();
            string text = Fixtures.Record().Replay.GetRawText().Replace("\"operationIndex\":0", "\"operationIndex\":-0", StringComparison.Ordinal);
            string file = directory.File("negative-zero.json");
            File.WriteAllText(file, text);
            using var output = new StringWriter();
            int exit = new CliRunner(Fixtures.Registry(), _ => throw new InvalidOperationException("Must not execute")).Run(
                new[] { "replay", "--input", file }, output, TextWriter.Null);
            Assert.Equal(2, exit);
            Assert.Equal("$.operationIndex", Fixtures.Json(output.ToString()).GetProperty("location").GetProperty("path").GetString());
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("-1", -1)]
        [InlineData("2147483647", int.MaxValue)]
        [InlineData("-2147483648", int.MinValue)]
        public void CanonicalInt32BoundariesRemainValid(string text, int expected)
        {
            Assert.Equal(expected, ProtocolJson.Int32(Fixtures.Json(text), "$.value"));
        }

        private sealed class BrokenEncoder : HostedGame<GridCommand, GridGameView, GridGameState>
        {
            private readonly string _fault;
            internal BrokenEncoder(SimulationRuntime<GridCommand, GridGameView, GridGameState> runtime,
                SequentialPresentationQueue<GridGameView> queue, string fault = "event") : base(runtime, queue) { _fault = fault; }
            protected override GridCommand ReadCommand(JsonElement command) => new EndTurn(Side.A);
            protected override void WriteObservation(Utf8JsonWriter writer, GridGameView observation)
            {
                if (_fault == "observation")
                { throw new InvalidOperationException("Invalid observation encoding"); }
                writer.WriteNullValue();
            }
            protected override void WriteState(Utf8JsonWriter writer, GridGameState state)
            {
                if (_fault == "state")
                { throw new InvalidOperationException("Invalid state encoding"); }
                writer.WriteNullValue();
            }
            protected override void WriteEvent(Utf8JsonWriter writer, GameEvent value) => throw new InvalidOperationException("Unknown event");
        }

        private sealed class FaultModule : IGameModule
        {
            private readonly GridGameModule _inner = new GridGameModule();
            private readonly string _fault;
            internal FaultModule(string fault) { _fault = fault; }
            internal FaultGame? Candidate { get; private set; }
            public string Id => _inner.Id;
            public int Version => _inner.Version;
            public SimulationTiming Timing => _inner.Timing;
            public JsonElement Description => _inner.Description;
            public IReadOnlyList<Type> CodeTypes => _inner.CodeTypes;
            public IReadOnlyList<string> NativeLibraries => _inner.NativeLibraries;
            public JsonElement ReadConfiguration(JsonElement value) => _inner.ReadConfiguration(value);
            public JsonElement ReadCommand(JsonElement value) => _inner.ReadCommand(value);
            public JsonElement ReadState(JsonElement configuration, JsonElement state, long processed) => _inner.ReadState(configuration, state, processed);
            public void ValidateResult(JsonElement result)
            {
                if (_fault == "result")
                { throw new ProtocolException("InvalidResult", "Unknown rejection code", "$.result"); }
                _inner.ValidateResult(result);
            }
            public IHostedGame Create(JsonElement configuration, ulong seed)
            {
                if (_fault == "create")
                { throw new ProtocolException("InvalidCreatedState", "Invalid module output", "$.state"); }
                return new FaultGame(_inner.Create(configuration, seed), _fault);
            }
            public IHostedGame Restore(JsonElement configuration, JsonElement state, RuntimeState runtime)
            {
                Candidate = new FaultGame(_inner.Restore(configuration, state, runtime), _fault);
                return Candidate;
            }
        }

        private sealed class FaultGame : IHostedGame
        {
            private readonly IHostedGame _inner;
            private readonly string _fault;
            internal FaultGame(IHostedGame inner, string fault) { _inner = inner; _fault = fault; }
            internal int Disposals { get; private set; }
            public long Tick => _inner.Tick + (_fault == "tick" ? 1 : 0);
            public long Submitted => _inner.Submitted + (_fault == "submitted" ? 1 : 0);
            public long Processed => _inner.Processed + (_fault == "processed" ? 1 : 0);
            public int Pending => _inner.Pending + (_fault == "pending" ? 1 : 0);
            public ulong RandomState => _inner.RandomState ^ (_fault == "random" ? 1UL : 0UL);
            public SimulationTiming Timing => _fault == "timing" ? new SimulationTiming(60) : _inner.Timing;
            public long Submit(JsonElement command) => _inner.Submit(command);
            public JsonElement Advance()
            {
                JsonElement result = _inner.Advance();
                if (_fault == "json")
                {
                    return JsonData.Write(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("duplicate", 1);
                        writer.WriteNumber("duplicate", 2);
                        writer.WriteEndObject();
                    });
                }
                return result;
            }
            public JsonElement Observe() => _inner.Observe();
            public JsonElement CaptureGameState() => _fault == "state" ? JsonData.EmptyObject : _inner.CaptureGameState();
            public void Dispose() { Disposals++; _inner.Dispose(); }
        }
    }
}
