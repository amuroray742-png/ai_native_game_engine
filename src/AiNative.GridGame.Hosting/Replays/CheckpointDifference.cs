using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiNative.GridGame.Hosting
{
    public sealed class FieldDifference
    {
        internal FieldDifference(string path, string kind, JsonElement? expected, JsonElement? actual)
        {
            Path = path;
            Kind = kind;
            Expected = expected;
            Actual = actual;
        }

        public string Path { get; }
        public string Kind { get; }
        public JsonElement? Expected { get; }
        public JsonElement? Actual { get; }
    }

    internal sealed class ReplayDivergence
    {
        internal ReplayDivergence(string expectedHash, string actualHash, IReadOnlyList<FieldDifference> differences)
        {
            ExpectedHash = expectedHash;
            ActualHash = actualHash;
            Differences = new List<FieldDifference>(differences).AsReadOnly();
        }

        internal string ExpectedHash { get; }
        internal string ActualHash { get; }
        internal IReadOnlyList<FieldDifference> Differences { get; }

        internal void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("expectedHash", ExpectedHash);
            writer.WriteString("actualHash", ActualHash);
            writer.WriteStartArray("differences");
            foreach (FieldDifference difference in Differences)
            {
                writer.WriteStartObject();
                writer.WriteString("path", difference.Path);
                writer.WriteString("kind", difference.Kind);
                writer.WritePropertyName("expected");
                WriteValue(writer, difference.Expected);
                writer.WritePropertyName("actual");
                WriteValue(writer, difference.Actual);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteValue(Utf8JsonWriter writer, JsonElement? value)
        {
            if (value.HasValue)
            {
                value.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    internal static class CheckpointDifference
    {
        internal static ReplayDivergence? Compare(GameCheckpoint expected, GameCheckpoint actual)
        {
            using JsonDocument left = JsonDocument.Parse(CanonicalState.WriteBytes(expected.Write));
            using JsonDocument right = JsonDocument.Parse(CanonicalState.WriteBytes(actual.Write));
            var differences = new List<FieldDifference>();
            foreach (string field in new[] { "state", "session", "result" })
            {
                CompareValue(left.RootElement.GetProperty(field), right.RootElement.GetProperty(field), field, differences);
            }
            if (differences.Count == 0)
            {
                return null;
            }
            return new ReplayDivergence(expected.StateHash, actual.StateHash,
                differences.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Kind, StringComparer.Ordinal).ToArray());
        }

        private static void CompareValue(JsonElement? expected, JsonElement? actual, string path, List<FieldDifference> differences)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                differences.Add(new FieldDifference(path, expected.HasValue ? "Missing" : "Added", expected?.Clone(), actual?.Clone()));
                return;
            }
            JsonElement left = expected.Value;
            JsonElement right = actual.Value;
            if (left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object)
            {
                var names = new SortedSet<string>(left.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
                names.UnionWith(right.EnumerateObject().Select(property => property.Name));
                foreach (string name in names)
                {
                    CompareValue(left.TryGetProperty(name, out JsonElement l) ? l : null,
                        right.TryGetProperty(name, out JsonElement r) ? r : null, path + "." + name, differences);
                }
            }
            else if (left.ValueKind == JsonValueKind.Array && right.ValueKind == JsonValueKind.Array)
            {
                if (path == "state.game.units")
                {
                    var leftUnits = left.EnumerateArray().ToDictionary(unit => unit.GetProperty("id").GetInt32());
                    var rightUnits = right.EnumerateArray().ToDictionary(unit => unit.GetProperty("id").GetInt32());
                    foreach (int id in leftUnits.Keys.Union(rightUnits.Keys))
                    {
                        CompareValue(leftUnits.TryGetValue(id, out JsonElement l) ? l : null,
                            rightUnits.TryGetValue(id, out JsonElement r) ? r : null, $"{path}[id={id}]", differences);
                    }
                }
                else
                {
                    for (int index = 0; index < Math.Max(left.GetArrayLength(), right.GetArrayLength()); index++)
                    {
                        CompareValue(index < left.GetArrayLength() ? left[index] : null,
                            index < right.GetArrayLength() ? right[index] : null, $"{path}[{index}]", differences);
                    }
                }
            }
            else if (!JsonElement.DeepEquals(left, right))
            {
                differences.Add(new FieldDifference(path, "Changed", left.Clone(), right.Clone()));
            }
        }
    }
}
