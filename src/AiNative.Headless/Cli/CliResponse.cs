using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AiNative.Headless
{
    internal sealed class CliResponse
    {
        internal string Category { get; private set; } = "Completed";
        internal string Code { get; set; } = "RunCompleted";
        internal string Message { get; private set; } = "执行完成。";
        internal int? OperationIndex { get; set; }
        internal long? Sequence { get; set; }
        internal string? ErrorPath { get; private set; }
        internal int Accepted { get; set; }
        internal int Rejected { get; set; }
        internal int ComparedCheckpoints { get; set; }
        internal ReplayDivergence? Divergence { get; set; }
        internal CliCheckpoint? Initial { get; set; }
        internal List<CliCheckpoint> Checkpoints { get; } = new List<CliCheckpoint>();
        internal List<string> SnapshotArtifacts { get; } = new List<string>();
        internal List<string> ReplayArtifacts { get; } = new List<string>();
        internal int ExitCode => Category switch
        {
            "Completed" => 0,
            "InputError" => 2,
            "ReplayDivergence" => 3,
            _ => 1
        };

        internal void Fail(HostOperationException error)
        {
            Category = error.Category;
            Code = error.Code;
            Message = error.Message;
            ErrorPath = error.Path;
            const string prefix = "$.operations[";
            if (!OperationIndex.HasValue && error.Path != null && error.Path.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                int end = error.Path.IndexOf(']', prefix.Length);
                if (end > prefix.Length && int.TryParse(error.Path.AsSpan(prefix.Length, end - prefix.Length), out int index))
                {
                    OperationIndex = index;
                }
            }
        }

        internal string Encode() => Encoding.UTF8.GetString(CanonicalState.WriteBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", 1);
            writer.WriteString("status", Category == "Completed" ? "Completed" : "Failed");
            writer.WriteString("category", Category);
            writer.WriteString("code", Code);
            writer.WriteString("message", Message);
            if (OperationIndex.HasValue || Sequence.HasValue || ErrorPath != null)
            {
                writer.WriteStartObject("location");
                if (OperationIndex.HasValue)
                {
                    writer.WriteNumber("operationIndex", OperationIndex.Value);
                }
                else
                {
                    writer.WriteNull("operationIndex");
                }
                writer.WriteString("sequence", Sequence?.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("path", ErrorPath);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("location");
            }
            writer.WriteNumber("accepted", Accepted);
            writer.WriteNumber("rejected", Rejected);
            writer.WriteNumber("comparedCheckpoints", ComparedCheckpoints);
            writer.WritePropertyName("initialCheckpoint");
            if (Initial == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                Initial.Write(writer);
            }
            writer.WriteStartArray("checkpoints");
            foreach (CliCheckpoint checkpoint in Checkpoints)
            {
                checkpoint.Write(writer);
            }
            writer.WriteEndArray();
            writer.WritePropertyName("finalCheckpoint");
            CliCheckpoint? final = Checkpoints.Count == 0 ? Initial : Checkpoints[Checkpoints.Count - 1];
            if (final == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                final.Write(writer);
            }
            writer.WriteStartArray("artifacts");
            foreach (string path in SnapshotArtifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "snapshot");
                writer.WriteString("path", path);
                writer.WriteEndObject();
            }
            foreach (string path in ReplayArtifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "replay");
                writer.WriteString("path", path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("divergence");
            if (Divergence == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                Divergence.Write(writer);
            }
            writer.WriteEndObject();
        }));
    }
}
