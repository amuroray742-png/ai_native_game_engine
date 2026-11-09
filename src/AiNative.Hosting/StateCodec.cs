using System;
using System.Globalization;
using System.Text.Json;
using AiNative.Core;

namespace AiNative.Hosting
{
    public sealed class SnapshotData
    {
        internal SnapshotData(IGameModule module, JsonElement configuration, RuntimeState runtime,
            JsonElement state, HostEnvironment environment, JsonElement json)
        {
            Module = module;
            Configuration = configuration;
            Runtime = runtime;
            State = state;
            Environment = environment;
            Json = json;
        }
        public IGameModule Module { get; }
        public JsonElement Configuration { get; }
        public RuntimeState Runtime { get; }
        public JsonElement State { get; }
        public HostEnvironment Environment { get; }
        public JsonElement Json { get; }
    }

    public static class StateCodec
    {
        public static JsonElement Runtime(IHostedGame game) => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("tick", Text(game.Tick));
            writer.WriteString("submitted", Text(game.Submitted));
            writer.WriteString("processed", Text(game.Processed));
            writer.WriteNumber("pending", game.Pending);
            writer.WriteNumber("ticksPerSecond", game.Timing.TicksPerSecond);
            writer.WriteString("randomAlgorithm", DeterministicRandom.Algorithm);
            writer.WriteString("randomState", game.RandomState.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        });

        public static JsonElement Encode(IGameModule module, JsonElement configuration, IHostedGame game, HostEnvironment environment)
        {
            JsonData.Require(game.Pending == 0, "PendingCommands", "$.runtime.pending");
            JsonElement runtime = Runtime(game), state = game.CaptureGameState();
            string configHash = JsonData.Hash(configuration);
            return JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 2);
                writer.WriteString("gameId", module.Id);
                writer.WriteNumber("gameVersion", module.Version);
                writer.WritePropertyName("configuration");
                configuration.WriteTo(writer);
                writer.WriteString("configHash", configHash);
                writer.WritePropertyName("runtime");
                runtime.WriteTo(writer);
                writer.WritePropertyName("state");
                state.WriteTo(writer);
                writer.WriteString("stateHash", Digest(module, configHash, runtime, state));
                writer.WritePropertyName("environment");
                environment.ToJson().WriteTo(writer);
                writer.WriteEndObject();
            });
        }

        public static SnapshotData Read(JsonElement root, GameRegistry registry)
        {
            JsonData.Version(root);
            ProtocolJson.Object(root, "$", "formatVersion", "gameId", "gameVersion", "configuration", "configHash",
                "runtime", "state", "stateHash", "environment");
            IGameModule module = registry.Get(ProtocolJson.String(root.GetProperty("gameId"), "$.gameId"));
            JsonData.Require(ProtocolJson.Int32(root.GetProperty("gameVersion"), "$.gameVersion") == module.Version,
                "UnsupportedGameVersion", "$.gameVersion");
            JsonElement configuration = module.ReadConfiguration(root.GetProperty("configuration"));
            string configHash = ProtocolJson.Hash(root.GetProperty("configHash"), "$.configHash");
            JsonData.Require(JsonData.Hash(configuration) == configHash, "ConfigHashMismatch", "$.configHash");
            JsonElement runtime = root.GetProperty("runtime"), state = root.GetProperty("state");
            ValidateRuntime(runtime, module);
            JsonData.Require(Count(runtime, "pending") == 0, "PendingCommands", "$.runtime.pending");
            RuntimeState saved;
            try
            {
                saved = new RuntimeState(Long(runtime, "tick"), Long(runtime, "submitted"), Long(runtime, "processed"),
                    ProtocolJson.UInt64(runtime.GetProperty("randomState"), "$.runtime.randomState"), module.Timing);
            }
            catch (ArgumentException error) { throw new ProtocolException("InvalidSnapshot", error.Message, "$.runtime", error); }
            state = module.ReadState(configuration, state, saved.Processed);
            root = JsonData.WithProperty(JsonData.WithProperty(root, "configuration", configuration), "state", state);
            JsonData.Require(Digest(module, configHash, runtime, state) == ProtocolJson.Hash(root.GetProperty("stateHash"), "$.stateHash"),
                "StateHashMismatch", "$.stateHash");
            HostEnvironment environment = HostEnvironment.Read(root.GetProperty("environment"));
            return new SnapshotData(module, configuration.Clone(), saved, state.Clone(), environment, root.Clone());
        }

        public static IHostedGame Restore(SnapshotData snapshot, HostEnvironment environment)
        {
            snapshot.Environment.RequireMatch(environment);
            IHostedGame? candidate = null;
            try
            {
                candidate = snapshot.Module.Restore(snapshot.Configuration, snapshot.State, snapshot.Runtime);
                JsonData.Require(JsonData.Hash(Runtime(candidate)) == JsonData.Hash(snapshot.Json.GetProperty("runtime")),
                    "RestoreRuntimeMismatch", "$.runtime");
                JsonData.Require(JsonData.Hash(candidate.CaptureGameState()) == JsonData.Hash(snapshot.State),
                    "RestoreStateMismatch", "$.state");
                return candidate;
            }
            catch { candidate?.Dispose(); throw; }
        }

        public static string Digest(IGameModule module, string configHash, JsonElement runtime, JsonElement state) =>
            JsonData.Hash(JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("gameId", module.Id);
                writer.WriteNumber("gameVersion", module.Version);
                writer.WriteString("configHash", configHash);
                writer.WritePropertyName("runtime");
                runtime.WriteTo(writer);
                writer.WritePropertyName("state");
                state.WriteTo(writer);
                writer.WriteEndObject();
            }));

        public static void ValidateRuntime(JsonElement value, IGameModule module)
        {
            ProtocolJson.Object(value, "$.runtime", "tick", "submitted", "processed", "pending", "ticksPerSecond", "randomAlgorithm", "randomState");
            long tick = Long(value, "tick"), submitted = Long(value, "submitted"), processed = Long(value, "processed");
            int pending = Count(value, "pending");
            JsonData.Require(processed <= submitted && pending >= 0 && submitted - processed == pending
                && (tick != 0 || processed == 0), "InvalidRuntime", "$.runtime");
            JsonData.Require(Count(value, "ticksPerSecond") == module.Timing.TicksPerSecond, "TimingMismatch", "$.runtime.ticksPerSecond");
            JsonData.Require(ProtocolJson.String(value.GetProperty("randomAlgorithm"), "$.runtime.randomAlgorithm") == DeterministicRandom.Algorithm,
                "UnsupportedRandomAlgorithm", "$.runtime.randomAlgorithm");
            ProtocolJson.UInt64(value.GetProperty("randomState"), "$.runtime.randomState");
        }

        public static long Long(JsonElement value, string field) => ProtocolJson.Int64(value.GetProperty(field), "$.runtime." + field);
        public static int Count(JsonElement value, string field) => ProtocolJson.Int32(value.GetProperty(field), "$.runtime." + field);
        public static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
