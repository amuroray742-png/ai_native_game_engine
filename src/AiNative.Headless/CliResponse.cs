using System.Collections.Generic;
using System.Text.Json;
using AiNative.Hosting;

namespace AiNative.Headless
{
    internal sealed class CliResponse
    {
        internal string Category { get; set; } = "Completed";
        internal string Code { get; set; } = "RunCompleted";
        internal string Message { get; set; } = "执行完成。";
        internal string? Path { get; set; }
        internal int? Operation { get; set; }
        internal int Accepted { get; set; }
        internal int Rejected { get; set; }
        internal int Compared { get; set; }
        internal JsonElement? InitialCheckpoint { get; set; }
        internal List<JsonElement> Checkpoints { get; } = new List<JsonElement>();
        internal JsonElement? Observation { get; set; }
        internal JsonElement? Data { get; set; }
        internal IReadOnlyList<FieldDifference> Differences { get; set; } = System.Array.Empty<FieldDifference>();
        internal List<string> Artifacts { get; } = new List<string>();
        internal int ExitCode => Category == "Completed" ? 0 : Category == "InputError" ? 2 : Category == "ReplayDivergence" ? 3 : 1;

        internal JsonElement Encode() => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 2);
            writer.WriteString("status", Category == "Completed" ? "Completed" : "Failed");
            writer.WriteString("category", Category);
            writer.WriteString("code", Code);
            writer.WriteString("message", Message);
            writer.WriteNumber("accepted", Accepted);
            writer.WriteNumber("rejected", Rejected);
            writer.WriteNumber("comparedCheckpoints", Compared);
            writer.WritePropertyName("location");
            if (Path == null && Operation == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("path", Path);
                if (Operation.HasValue)
                {
                    writer.WriteNumber("operationIndex", Operation.Value);
                }
                else
                {
                    writer.WriteNull("operationIndex");
                }
                writer.WriteEndObject();
            }
            WriteOptional(writer, "initialCheckpoint", InitialCheckpoint);
            writer.WriteStartArray("checkpoints");
            foreach (JsonElement checkpoint in Checkpoints)
            {
                checkpoint.WriteTo(writer);
            }
            writer.WriteEndArray();
            WriteOptional(writer, "finalCheckpoint", Checkpoints.Count > 0 ? Checkpoints[Checkpoints.Count - 1] : InitialCheckpoint);
            WriteOptional(writer, "observation", Observation);
            WriteOptional(writer, "data", Data);
            writer.WriteStartArray("divergence");
            foreach (FieldDifference difference in Differences)
            {
                writer.WriteStartObject();
                writer.WriteString("path", difference.Path);
                writer.WriteString("expected", difference.Expected);
                writer.WriteString("actual", difference.Actual);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("artifacts");
            foreach (string artifact in Artifacts)
            {
                writer.WriteStringValue(artifact);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        private static void WriteOptional(Utf8JsonWriter writer, string name, JsonElement? value)
        {
            writer.WritePropertyName(name);
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
}
