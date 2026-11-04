using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiNative.Hosting
{
    public sealed class RecordedRun
    {
        internal RecordedRun(JsonElement replay, JsonElement snapshot, JsonElement observation, int accepted, int rejected)
        {
            Replay = replay;
            Snapshot = snapshot;
            Observation = observation;
            Accepted = accepted;
            Rejected = rejected;
        }
        public JsonElement Replay { get; }
        public JsonElement Snapshot { get; }
        public JsonElement Observation { get; }
        public int Accepted { get; }
        public int Rejected { get; }
    }

    public sealed class ReplayMismatchException : Exception
    {
        internal ReplayMismatchException(int operationIndex, IReadOnlyList<FieldDifference> differences, int accepted, int rejected)
            : base("回放在首个检查点分歧处停止。")
        {
            OperationIndex = operationIndex;
            Differences = differences;
            Accepted = accepted;
            Rejected = rejected;
        }
        public int OperationIndex { get; }
        public IReadOnlyList<FieldDifference> Differences { get; }
        public int Accepted { get; }
        public int Rejected { get; }
    }

    public sealed class FieldDifference
    {
        internal FieldDifference(string path, string expected, string actual)
        {
            Path = path;
            Expected = expected;
            Actual = actual;
        }
        public string Path { get; }
        public string Expected { get; }
        public string Actual { get; }
    }

    public sealed class HostComparison
    {
        internal HostComparison(HostEnvironment expected, HostEnvironment actual, int matched, int? operation, IReadOnlyList<FieldDifference> differences)
        {
            ExpectedEnvironment = expected;
            ActualEnvironment = actual;
            ComparedCheckpoints = matched;
            OperationIndex = operation;
            Differences = differences;
        }
        public HostEnvironment ExpectedEnvironment { get; }
        public HostEnvironment ActualEnvironment { get; }
        public int ComparedCheckpoints { get; }
        public int? OperationIndex { get; }
        public IReadOnlyList<FieldDifference> Differences { get; }
        public bool Matched => Differences.Count == 0;
    }

    public static class HostVerification
    {
        public static RecordedRun Record(IGameModule module, JsonElement configuration, IHostedGame game,
            CommandFile commands, HostEnvironment environment, ReplayRecord? expected = null,
            Action<JsonElement, int, int>? checkpointCompleted = null, Action<int>? operationStarting = null)
        {
            JsonElement initial = StateCodec.Encode(module, configuration, game, environment);
            JsonElement first = Checkpoint(module, configuration, game, null, Kind("initial"));
            if (expected != null)
            {
                JsonData.Require(JsonData.Hash(expected.Commands.Json) == JsonData.Hash(commands.Json)
                    && expected.Initial.Module.Id == module.Id && JsonData.Hash(expected.InitialCheckpoint) == JsonData.Hash(first),
                    "ComparisonInputMismatch", "$.initialCheckpoint");
            }
            var checkpoints = new List<JsonElement>();
            int accepted = 0, rejected = 0;
            checkpointCompleted?.Invoke(first, accepted, rejected);
            foreach (CommandOperation operation in commands.Operations)
            {
                operationStarting?.Invoke(checkpoints.Count);
                JsonElement result;
                if (operation.Command.HasValue)
                {
                    long sequence = game.Submit(operation.Command.Value);
                    result = JsonData.Write(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", "submitted");
                        writer.WriteString("sequence", StateCodec.Text(sequence));
                        writer.WriteEndObject();
                    });
                }
                else
                {
                    result = game.Advance();
                    foreach (JsonElement input in result.GetProperty("inputs").EnumerateArray())
                    {
                        if (input.GetProperty("accepted").GetBoolean())
                        {
                            accepted++;
                        }
                        else
                        {
                            rejected++;
                        }
                    }
                }
                JsonElement checkpoint = Checkpoint(module, configuration, game, checkpoints.Count, result);
                // Publish only fully captured checkpoints. A later fault preserves this trusted prefix.
                checkpointCompleted?.Invoke(checkpoint, accepted, rejected);
                if (expected != null)
                {
                    IReadOnlyList<FieldDifference> differences = Differences(expected.Checkpoints[checkpoints.Count], checkpoint);
                    if (differences.Count != 0)
                    {
                        throw new ReplayMismatchException(checkpoints.Count, differences, accepted, rejected);
                    }
                }
                checkpoints.Add(checkpoint);
            }
            JsonElement replay = JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 2);
                writer.WritePropertyName("initialSnapshot");
                initial.WriteTo(writer);
                writer.WritePropertyName("commands");
                commands.Json.WriteTo(writer);
                writer.WritePropertyName("initialCheckpoint");
                first.WriteTo(writer);
                writer.WriteStartArray("checkpoints");
                foreach (JsonElement checkpoint in checkpoints)
                {
                    checkpoint.WriteTo(writer);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return new RecordedRun(replay, StateCodec.Encode(module, configuration, game, environment), game.Observe(), accepted, rejected);
        }

        public static HostComparison Compare(ReplayRecord expected, ReplayRecord actual)
        {
            JsonData.Require(expected.Initial.Module.Id == actual.Initial.Module.Id
                && expected.Initial.Module.Version == actual.Initial.Module.Version
                && expected.Initial.Json.GetProperty("configHash").GetString() == actual.Initial.Json.GetProperty("configHash").GetString()
                && expected.Initial.Json.GetProperty("stateHash").GetString() == actual.Initial.Json.GetProperty("stateHash").GetString()
                && JsonData.Hash(expected.Commands.Json) == JsonData.Hash(actual.Commands.Json), "ComparisonInputMismatch", "$");
            for (int index = 0; index < expected.Checkpoints.Count; index++)
            {
                IReadOnlyList<FieldDifference> differences = Differences(expected.Checkpoints[index], actual.Checkpoints[index]);
                if (differences.Count != 0)
                {
                    return new HostComparison(expected.Initial.Environment, actual.Initial.Environment, index + 1, index, differences);
                }
            }
            return new HostComparison(expected.Initial.Environment, actual.Initial.Environment,
                expected.Checkpoints.Count + 1, null, Array.Empty<FieldDifference>());
        }

        public static IReadOnlyList<FieldDifference> Differences(JsonElement expected, JsonElement actual)
        {
            var differences = new List<FieldDifference>();
            CompareValue(expected, actual, "$", differences);
            return differences.AsReadOnly();
        }

        private static void CompareValue(JsonElement expected, JsonElement actual, string path, List<FieldDifference> differences)
        {
            if (differences.Count >= 32)
            {
                return;
            }
            if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
            {
                var names = expected.EnumerateObject().Select(value => value.Name)
                    .Union(actual.EnumerateObject().Select(value => value.Name)).OrderBy(value => value, StringComparer.Ordinal);
                foreach (string name in names)
                {
                    bool left = expected.TryGetProperty(name, out JsonElement a), right = actual.TryGetProperty(name, out JsonElement b);
                    if (left && right)
                    {
                        CompareValue(a, b, path + "." + name, differences);
                    }
                    else
                    {
                        differences.Add(new FieldDifference(path + "." + name, left ? Preview(a) : "<missing>", right ? Preview(b) : "<missing>"));
                    }
                    if (differences.Count >= 32)
                    {
                        break;
                    }
                }
                return;
            }
            if (expected.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array
                && expected.GetArrayLength() == actual.GetArrayLength())
            {
                for (int index = 0; index < expected.GetArrayLength() && differences.Count < 32; index++)
                {
                    CompareValue(expected[index], actual[index], path + "[" + index + "]", differences);
                }
                return;
            }
            if (JsonData.Hash(expected) != JsonData.Hash(actual))
            {
                differences.Add(new FieldDifference(path, Preview(expected), Preview(actual)));
            }
        }

        private static string Preview(JsonElement value)
        {
            string text = value.GetRawText();
            return text.Length <= 256 ? text : text.Substring(0, 128) + "… sha256=" + JsonData.Hash(value);
        }

        private static JsonElement Checkpoint(IGameModule module, JsonElement configuration, IHostedGame game, int? index, JsonElement result)
        {
            JsonElement runtime = StateCodec.Runtime(game), state = game.CaptureGameState();
            return JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                if (index.HasValue)
                {
                    writer.WriteNumber("operationIndex", index.Value);
                }
                else
                {
                    writer.WriteNull("operationIndex");
                }
                writer.WritePropertyName("runtime");
                runtime.WriteTo(writer);
                writer.WritePropertyName("state");
                state.WriteTo(writer);
                writer.WriteString("stateHash", StateCodec.Digest(module, JsonData.Hash(configuration), runtime, state));
                writer.WritePropertyName("result");
                result.WriteTo(writer);
                writer.WriteEndObject();
            });
        }

        private static JsonElement Kind(string kind) => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteEndObject();
        });
    }
}
