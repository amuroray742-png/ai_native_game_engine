using System;
using System.IO;
using System.Text.Json.Nodes;
using AiNative.Headless;

namespace AiNative.Tests
{
    internal static class SnapshotFixtures
    {
        internal static SnapshotEnvironment Environment => new SnapshotEnvironment("0.0.0", "linux", "x64",
            "964789b064e8cea5fb7fbbeb6083ed20d0e33eea5a1c5f3bcecc236890837be9");

        internal static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SnapshotExamples", name));

        internal static JsonObject Initial() => JsonNode.Parse(Read("initial.snapshot.json"))?.AsObject()
            ?? throw new InvalidOperationException("Missing fixture.");

        internal static void Set(JsonObject root, string path, JsonNode? value)
        {
            string[] parts = path.Split('.');
            JsonNode parent = root;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                parent = parent[parts[index]] ?? throw new InvalidOperationException(path);
            }
            parent[parts[parts.Length - 1]] = value;
        }
    }

    internal sealed class SnapshotDirectory : IDisposable
    {
        internal SnapshotDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainative-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        internal string FilePath => System.IO.Path.Combine(Path, "state.json");
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
