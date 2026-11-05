using System;
using System.IO;
using System.Text.Json;
using AiNative.Hosting;

namespace AiNative.Headless
{
    public sealed class CliRunner
    {
        private readonly GameRegistry _registry;
        private readonly Func<IGameModule, HostEnvironment> _environment;
        public CliRunner(GameRegistry registry, Func<IGameModule, HostEnvironment>? environment = null)
        {
            _registry = registry;
            _environment = environment ?? BuildEnvironment.Current;
        }

        public int Run(string[] args, TextWriter output, TextWriter error)
        {
            var response = new CliResponse();
            try
            {
                CliOptions options = CliOptions.Parse(args);
                if (options.Mode == "help")
                {
                    output.WriteLine("v2: games | describe --game ID | run (--scenario FILE --seed N | --snapshot FILE) --commands FILE [--snapshot-out FILE] [--replay-out FILE] | inspect --snapshot FILE | replay --input FILE | compare --expected FILE --actual FILE");
                    return 0;
                }
                Execute(options, response);
            }
            catch (ProtocolException failure)
            {
                response.Category = "InputError";
                response.Code = failure.Code;
                response.Path = failure.Path;
                response.Message = failure.Message;
            }
            catch (HostOperationException failure)
            {
                response.Category = failure.Category;
                response.Code = failure.Code;
                response.Path = failure.Path;
                response.Message = failure.Message;
            }
            catch (ReplayMismatchException failure)
            {
                response.Category = "ReplayDivergence";
                response.Code = "CheckpointMismatch";
                response.Message = failure.Message;
                response.Operation = failure.OperationIndex;
                response.Compared = failure.OperationIndex + 1;
                response.Differences = failure.Differences;
                response.Accepted = failure.Accepted;
                response.Rejected = failure.Rejected;
            }
            catch (Exception failure) when (failure is DllNotFoundException || failure is EntryPointNotFoundException || failure is BadImageFormatException)
            {
                response.Category = "HostFailure";
                response.Code = failure is BadImageFormatException ? "NativeLibraryIncompatible" : "NativeLibraryUnavailable";
                response.Message = failure.Message;
            }
            catch (Exception failure)
            {
                response.Category = "SimulationFailure";
                response.Code = "SimulationFault";
                response.Message = failure.Message;
            }
            if (response.ExitCode != 0)
            {
                try
                {
                    error.WriteLine(response.Code + ": " + response.Message);
                }
                catch (Exception diagnostic) when (diagnostic is IOException || diagnostic is ObjectDisposedException) { }
            }
            try
            {
                output.WriteLine(response.Encode().GetRawText());
            }
            catch (Exception failure) when (failure is IOException || failure is ObjectDisposedException)
            {
                try
                {
                    error.WriteLine("OutputWriteFailed: " + failure.Message);
                }
                catch (Exception diagnostic) when (diagnostic is IOException || diagnostic is ObjectDisposedException) { }
                return 1;
            }
            return response.ExitCode;
        }

        private void Execute(CliOptions options, CliResponse response)
        {
            if (options.Mode == "games" || options.Mode == "describe")
            {
                response.Code = options.Mode == "games" ? "GamesListed" : "GameDescribed";
                response.Data = options.Mode == "describe" ? _registry.Get(options.Required("--game")).Description : JsonData.Write(writer =>
                {
                    writer.WriteStartArray();
                    foreach (IGameModule module in _registry.Modules)
                    {
                        module.Description.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                });
                return;
            }
            if (options.Mode == "compare")
            {
                ReplayRecord expected = ReplayRecord.Read(Read(options.Required("--expected")), _registry);
                ReplayRecord actual = ReplayRecord.Read(Read(options.Required("--actual")), _registry);
                HostComparison comparison = HostVerification.Compare(expected, actual);
                response.Category = comparison.Matched ? "Completed" : "ReplayDivergence";
                response.Code = comparison.Matched ? "HostTracesMatched" : "CheckpointMismatch";
                response.Compared = comparison.ComparedCheckpoints;
                response.Operation = comparison.OperationIndex;
                response.Differences = comparison.Differences;
                response.Data = JsonData.Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("expectedEnvironment");
                    comparison.ExpectedEnvironment.ToJson().WriteTo(writer);
                    writer.WritePropertyName("actualEnvironment");
                    comparison.ActualEnvironment.ToJson().WriteTo(writer);
                    writer.WriteEndObject();
                });
                return;
            }
            RunSimulation(options, response);
        }

        private void RunSimulation(CliOptions options, CliResponse response)
        {
            ReplayRecord? replay = options.Mode == "replay" ? ReplayRecord.Read(Read(options.Required("--input")), _registry) : null;
            SnapshotData? snapshot = replay?.Initial;
            if (options.Get("--snapshot") is string snapshotPath)
            {
                snapshot = StateCodec.Read(Read(snapshotPath), _registry);
            }
            IGameModule module;
            JsonElement configuration;
            if (snapshot != null)
            {
                module = snapshot.Module;
                configuration = snapshot.Configuration;
            }
            else
            {
                (module, configuration) = ReadScene(options.Required("--scenario"));
            }
            CommandFile commands = replay?.Commands ?? CommandFile.Read(options.Mode == "inspect" ? EmptyCommands() : Read(options.Required("--commands")), module);
            if (options.Get("--expect-config") is string expectedPath)
            {
                (IGameModule expectedModule, JsonElement expected) = ReadScene(expectedPath);
                JsonData.Require(module.Id == expectedModule.Id && JsonData.Hash(expected) == JsonData.Hash(configuration),
                    "ExpectedConfigMismatch", "$.configuration");
            }
            HostEnvironment environment = _environment(module);
            snapshot?.Environment.RequireMatch(environment);
            string? snapshotOutput = Preflight(options.Get("--snapshot-out")), replayOutput = Preflight(options.Get("--replay-out"));
            if (snapshotOutput != null && replayOutput != null && string.Equals(snapshotOutput, replayOutput,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new HostOperationException("InputError", "OutputCollision", "两个输出不能使用同一路径。");
            }
            using IHostedGame game = snapshot == null ? module.Create(configuration, options.Seed) : StateCodec.Restore(snapshot, environment);
            RecordedRun result = HostVerification.Record(module, configuration, game, commands, environment, replay,
                (checkpoint, accepted, rejected) =>
                {
                    if (checkpoint.GetProperty("operationIndex").ValueKind == JsonValueKind.Null)
                    {
                        response.InitialCheckpoint = checkpoint;
                    }
                    else
                    {
                        response.Checkpoints.Add(checkpoint);
                    }
                    response.Accepted = accepted;
                    response.Rejected = rejected;
                }, index => response.Operation = index);
            response.Operation = null;
            response.Observation = result.Observation;
            response.Accepted = result.Accepted;
            response.Rejected = result.Rejected;
            response.Code = options.Mode == "replay" ? "ReplayMatched" : options.Mode == "inspect" ? "SnapshotInspected" : "RunCompleted";
            if (replay != null)
            {
                response.Compared = replay.Checkpoints.Count + 1;
            }
            if (snapshotOutput != null)
            {
                response.Artifacts.Add(Publish(snapshotOutput, result.Snapshot));
            }
            if (replayOutput != null)
            {
                response.Artifacts.Add(Publish(replayOutput, result.Replay));
            }
        }

        private (IGameModule Module, JsonElement Configuration) ReadScene(string path) => _registry.ReadScenario(Read(path));

        private static JsonElement Read(string path) => JsonData.Parse(JsonInput.ReadFile(path));
        private static string? Preflight(string? path) => path == null ? null : AtomicFilePublisher.Preflight(path);
        private static string Publish(string path, JsonElement value) => AtomicFilePublisher.Publish(path, stream =>
        {
            byte[] bytes = JsonData.CanonicalBytes(value);
            stream.Write(bytes);
        });
        private static JsonElement EmptyCommands() => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 2);
            writer.WriteStartArray("operations");
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
