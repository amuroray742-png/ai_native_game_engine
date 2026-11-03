using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AiNative.Hosting
{
    public sealed class ReplayRecord
    {
        internal ReplayRecord(SnapshotData initial, CommandFile commands, JsonElement initialCheckpoint,
            IReadOnlyList<JsonElement> checkpoints, JsonElement json)
        {
            Initial = initial;
            Commands = commands;
            InitialCheckpoint = initialCheckpoint;
            Checkpoints = checkpoints;
            Json = json;
        }
        public SnapshotData Initial { get; }
        public CommandFile Commands { get; }
        public JsonElement InitialCheckpoint { get; }
        public IReadOnlyList<JsonElement> Checkpoints { get; }
        public JsonElement Json { get; }

        public static ReplayRecord Read(JsonElement root, GameRegistry registry)
        {
            JsonData.Version(root);
            ProtocolJson.Object(root, "$", "formatVersion", "initialSnapshot", "commands", "initialCheckpoint", "checkpoints");
            SnapshotData initial = StateCodec.Read(root.GetProperty("initialSnapshot"), registry);
            CommandFile commands = CommandFile.Read(root.GetProperty("commands"), initial.Module);
            JsonElement first = root.GetProperty("initialCheckpoint");
            first = ReadCheckpoint(first, initial, null);
            ProtocolJson.Object(first.GetProperty("result"), "$.result", "kind");
            JsonData.Require(ProtocolJson.String(first.GetProperty("result").GetProperty("kind"), "$.result.kind") == "initial"
                && JsonData.Hash(first.GetProperty("runtime")) == JsonData.Hash(initial.Json.GetProperty("runtime"))
                && JsonData.Hash(first.GetProperty("state")) == JsonData.Hash(initial.State), "InvalidReplay", "$.initialCheckpoint");
            var checkpoints = new List<JsonElement>();
            foreach (JsonElement checkpoint in ProtocolJson.Array(root.GetProperty("checkpoints"), "$.checkpoints"))
            {
                checkpoints.Add(checkpoint.Clone());
            }
            JsonData.Require(checkpoints.Count == commands.Operations.Count, "InvalidReplay", "$.checkpoints");
            JsonElement previous = first;
            for (int index = 0; index < checkpoints.Count; index++)
            {
                JsonElement current = checkpoints[index];
                current = ReadCheckpoint(current, initial, index);
                checkpoints[index] = current;
                ValidateTransition(previous, current, commands.Operations[index], initial.Module);
                previous = current;
            }
            return new ReplayRecord(initial, commands, first.Clone(), checkpoints.AsReadOnly(), root.Clone());
        }

        private static JsonElement ReadCheckpoint(JsonElement checkpoint, SnapshotData initial, int? index)
        {
            ProtocolJson.Object(checkpoint, "$.checkpoint", "operationIndex", "runtime", "state", "stateHash", "result");
            JsonElement position = checkpoint.GetProperty("operationIndex");
            JsonData.Require(index.HasValue ? ProtocolJson.Int32(position, "$.operationIndex") == index.Value
                : position.ValueKind == JsonValueKind.Null, "InvalidReplay", "$.operationIndex");
            JsonElement runtime = checkpoint.GetProperty("runtime"), state = checkpoint.GetProperty("state");
            StateCodec.ValidateRuntime(runtime, initial.Module);
            state = initial.Module.ReadState(initial.Configuration, state, StateCodec.Long(runtime, "processed"));
            string hash = StateCodec.Digest(initial.Module, initial.Json.GetProperty("configHash").GetString()!, runtime, state);
            JsonData.Require(hash == ProtocolJson.Hash(checkpoint.GetProperty("stateHash"), "$.stateHash"), "InvalidReplay", "$.stateHash");
            return JsonData.WithProperty(checkpoint, "state", state);
        }

        private static void ValidateTransition(JsonElement previous, JsonElement current, CommandOperation operation, IGameModule module)
        {
            JsonElement before = previous.GetProperty("runtime"), after = current.GetProperty("runtime"), result = current.GetProperty("result");
            long submitted = StateCodec.Long(before, "submitted"), processed = StateCodec.Long(before, "processed"), tick = StateCodec.Long(before, "tick");
            if (operation.Command.HasValue)
            {
                ProtocolJson.Object(result, "$.result", "kind", "sequence");
                JsonData.Require(submitted < long.MaxValue, "InvalidReplay", "$.runtime.submitted");
                JsonData.Require(ProtocolJson.String(result.GetProperty("kind"), "$.result.kind") == "submitted"
                    && ProtocolJson.Int64(result.GetProperty("sequence"), "$.result.sequence") == submitted + 1
                    && StateCodec.Long(after, "submitted") == submitted + 1 && StateCodec.Long(after, "processed") == processed
                    && StateCodec.Long(after, "tick") == tick
                    && before.GetProperty("randomState").GetString() == after.GetProperty("randomState").GetString()
                    && JsonData.Hash(previous.GetProperty("state")) == JsonData.Hash(current.GetProperty("state")),
                    "InvalidReplay", "$.result");
                return;
            }
            ProtocolJson.Object(result, "$.result", "kind", "tick", "inputs", "events");
            JsonData.Require(tick < long.MaxValue, "InvalidReplay", "$.runtime.tick");
            JsonData.Require(ProtocolJson.String(result.GetProperty("kind"), "$.result.kind") == "tick"
                && ProtocolJson.Int64(result.GetProperty("tick"), "$.result.tick") == tick + 1
                && StateCodec.Long(after, "tick") == tick + 1 && StateCodec.Long(after, "submitted") == submitted
                && StateCodec.Long(after, "processed") == submitted && StateCodec.Count(after, "pending") == 0,
                "InvalidReplay", "$.runtime");
            int count = 0;
            foreach (JsonElement input in ProtocolJson.Array(result.GetProperty("inputs"), "$.result.inputs"))
            {
                ProtocolJson.Object(input, "$.result.inputs", "sequence", "accepted", "error");
                JsonData.Require(ProtocolJson.Int64(input.GetProperty("sequence"), "$.sequence") == processed + count + 1,
                    "InvalidReplay", "$.sequence");
                JsonElement accepted = input.GetProperty("accepted"), error = input.GetProperty("error");
                JsonData.Require(accepted.ValueKind == JsonValueKind.True || accepted.ValueKind == JsonValueKind.False,
                    "InvalidReplay", "$.accepted");
                JsonData.Require(accepted.GetBoolean() ? error.ValueKind == JsonValueKind.Null
                    : error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()), "InvalidReplay", "$.error");
                count++;
            }
            JsonData.Require(count == submitted - processed, "InvalidReplay", "$.inputs");
            module.ValidateResult(result);
        }
    }
}
