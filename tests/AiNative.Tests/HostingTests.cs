using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.Core;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using AiNative.Hosting;
using AiNative.Physics;
using Xunit;

namespace AiNative.Tests
{
    public sealed class HostingTests
    {
        [Fact]
        public void EveryRequiredSnapshotFieldIsValidatedBeforeRestore()
        {
            JsonElement snapshot = Fixtures.Record().Snapshot;
            var paths = new List<string[]>();
            CollectRequiredFields(JsonNode.Parse(snapshot.GetRawText())!, Array.Empty<string>(), paths);
            foreach (string[] path in paths)
            {
                JsonElement damaged = Change(snapshot, node =>
                {
                    JsonNode parent = node;
                    foreach (string component in path.Take(path.Length - 1))
                    { parent = parent is JsonArray ? parent[int.Parse(component, CultureInfo.InvariantCulture)]! : parent[component]!; }
                    parent.AsObject().Remove(path.Last());
                });
                Assert.Throws<ProtocolException>(() => StateCodec.Read(damaged, Fixtures.Registry()));
            }
        }

        private static void CollectRequiredFields(JsonNode node, string[] path, List<string[]> paths)
        {
            if (node is JsonObject objectNode)
            {
                foreach (var property in objectNode)
                {
                    string[] child = path.Concat(new[] { property.Key }).ToArray();
                    paths.Add(child);
                    if (property.Value != null)
                    { CollectRequiredFields(property.Value, child, paths); }
                }
            }
            else if (node is JsonArray array && array.Count > 0 && array[0] != null)
            { CollectRequiredFields(array[0]!, path.Concat(new[] { "0" }).ToArray(), paths); }
        }

        internal static JsonElement Change(JsonElement source, Action<JsonNode> change)
        {
            JsonNode node = JsonNode.Parse(source.GetRawText())!;
            change(node);
            return Fixtures.Json(node.ToJsonString());
        }
        internal static void Rehash(JsonNode checkpoint, JsonElement snapshot)
        {
            IGameModule module = Fixtures.Registry().Get(snapshot.GetProperty("gameId").GetString()!);
            checkpoint["stateHash"] = StateCodec.Digest(module, snapshot.GetProperty("configHash").GetString()!,
                Fixtures.Json(checkpoint["runtime"]!.ToJsonString()), Fixtures.Json(checkpoint["state"]!.ToJsonString()));
        }

        [Theory]
        [InlineData("{\"x\":1,\"x\":2}")]
        [InlineData("{\"nested\":{\"x\":1,\"x\":2}}")]
        [InlineData("{\"x\":\"\\uD800\"}")]
        [InlineData("{\"x\":1,}")]
        [InlineData("/*comment*/{}")]
        [InlineData("{} {}")]
        public void JsonParserRejectsAmbiguousOrInvalidDocuments(string json)
        {
            Assert.Equal("InvalidJson", Assert.Throws<ProtocolException>(() => Fixtures.Json(json)).Code);
        }

        [Fact]
        public void InvalidUtf8IsRejectedAndCanonicalBytesAreCultureIndependent()
        {
            Assert.Throws<ProtocolException>(() => JsonData.Parse(new byte[] { 123, 34, 120, 34, 58, 34, 255, 34, 125 }));
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                JsonElement value = Fixtures.Json("{\"z\":1.5,\"a\":\"9007199254740993\"}");
                Assert.Equal("{\"a\":\"9007199254740993\",\"z\":1.5}", Encoding.UTF8.GetString(JsonData.CanonicalBytes(value)));
                Assert.Equal(JsonData.Hash(value), JsonData.Hash(Fixtures.Json("{\"a\":\"9007199254740993\",\"z\":1.5}")));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Theory]
        [InlineData("01")]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData("1.0")]
        [InlineData("1e3")]
        [InlineData(" 1")]
        [InlineData("١")]
        [InlineData("18446744073709551616")]
        public void WideIntegersRequireCanonicalDecimalStrings(string token)
        {
            Assert.Throws<ProtocolException>(() => ProtocolJson.UInt64(JsonSerializer.SerializeToElement(token), "$.value"));
        }

        [Theory]
        [InlineData("formatVersion", "1")]
        [InlineData("gameId", "\"unknown\"")]
        [InlineData("gameVersion", "9")]
        [InlineData("configHash", "\"bad\"")]
        [InlineData("stateHash", "\"bad\"")]
        [InlineData("state", "null")]
        [InlineData("environment", "null")]
        [InlineData("runtime", "{}")]
        [InlineData("configuration", "{}")]
        public void InvalidSnapshotEnvelopeFailsBeforeRestoration(string field, string json)
        {
            JsonElement original = Fixtures.Record(commands: Fixtures.Read("empty.commands.json")).Snapshot;
            JsonElement changed = Change(original, node => node[field] = JsonNode.Parse(json));
            Assert.Throws<ProtocolException>(() => StateCodec.Read(changed, Fixtures.Registry()));
        }

        [Theory]
        [InlineData("tick", "-1")]
        [InlineData("tick", "01")]
        [InlineData("tick", "9223372036854775808")]
        [InlineData("submitted", "1")]
        [InlineData("processed", "1")]
        [InlineData("randomState", "18446744073709551616")]
        [InlineData("randomAlgorithm", "unknown")]
        public void InvalidSnapshotRuntimeIsRejected(string field, string value)
        {
            JsonElement snapshot = Fixtures.Record(commands: Fixtures.Read("empty.commands.json")).Snapshot;
            Assert.Throws<ProtocolException>(() => StateCodec.Read(Change(snapshot, node => node["runtime"]![field] = value), Fixtures.Registry()));
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(ulong.MaxValue)]
        [InlineData(9007199254740993UL)]
        public void EmptyRecordAndSnapshotPreserveAllRandomBits(ulong seed)
        {
            RecordedRun run = Fixtures.Record(seed: seed, commands: Fixtures.Read("empty.commands.json"));
            SnapshotData saved = StateCodec.Read(run.Snapshot, Fixtures.Registry());
            Assert.Equal(seed, saved.Runtime.RandomState);
            using IHostedGame restored = StateCodec.Restore(saved, Fixtures.Environment());
            Assert.Equal(JsonData.Hash(run.Snapshot), JsonData.Hash(StateCodec.Encode(saved.Module, saved.Configuration, restored, saved.Environment)));
            ReplayRecord record = ReplayRecord.Read(run.Replay, Fixtures.Registry());
            Assert.Equal(1, HostVerification.Compare(record, record).ComparedCheckpoints);
        }

        [Fact]
        public void ConfigAndStateNormalizationPreservesPermutedEntitySemantics()
        {
            RecordedRun run = Fixtures.Record();
            JsonElement permuted = Change(run.Snapshot, node =>
            {
                foreach (string field in new[] { "configuration", "state" })
                {
                    JsonArray units = node[field]!["units"]!.AsArray();
                    JsonNode[] reversed = units.Reverse().Select(item => item!.DeepClone()).ToArray();
                    node[field]!["units"] = new JsonArray(reversed);
                }
            });
            SnapshotData saved = StateCodec.Read(permuted, Fixtures.Registry());
            using IHostedGame restored = StateCodec.Restore(saved, Fixtures.Environment());
            Assert.Equal(JsonData.Hash(run.Snapshot.GetProperty("state")), JsonData.Hash(restored.CaptureGameState()));
        }

        [Fact]
        public void EnvironmentExcludedFromStateButStrictForRestoreAndReplay()
        {
            RecordedRun first = Fixtures.Record(environment: "first"), second = Fixtures.Record(environment: "second");
            Assert.Equal(first.Snapshot.GetProperty("stateHash").GetString(), second.Snapshot.GetProperty("stateHash").GetString());
            Assert.True(HostVerification.Compare(ReplayRecord.Read(first.Replay, Fixtures.Registry()), ReplayRecord.Read(second.Replay, Fixtures.Registry())).Matched);
            SnapshotData saved = StateCodec.Read(first.Snapshot, Fixtures.Registry());
            Assert.Equal("EnvironmentMismatch", Assert.Throws<ProtocolException>(() => StateCodec.Restore(saved, Fixtures.Environment("second"))).Code);
        }

        [Theory]
        [InlineData("seed")]
        [InlineData("commands")]
        [InlineData("configuration")]
        public void CompareRejectsDifferentExperiments(string variant)
        {
            RecordedRun first = Fixtures.Record();
            RecordedRun second = variant == "seed" ? Fixtures.Record(seed: 7) : Fixtures.Record(commands: Fixtures.Read("empty.commands.json"));
            if (variant == "configuration")
            {
                IGameModule module = new GridGameModule();
                JsonElement config = module.ReadConfiguration(Change(Fixtures.Configuration, node => node["width"] = 8));
                using IHostedGame game = module.Create(config, 42);
                second = HostVerification.Record(module, config, game, CommandFile.Read(Fixtures.Read("grid.commands.json"), module), Fixtures.Environment());
            }
            Assert.Equal("ComparisonInputMismatch", Assert.Throws<ProtocolException>(() => HostVerification.Compare(
                ReplayRecord.Read(first.Replay, Fixtures.Registry()), ReplayRecord.Read(second.Replay, Fixtures.Registry()))).Code);
        }

        [Fact]
        public void ReplayStopsAtFirstValidEventDivergenceAndKeepsActualPrefix()
        {
            RecordedRun run = Fixtures.Record();
            int index = Enumerable.Range(0, run.Replay.GetProperty("checkpoints").GetArrayLength()).First(i =>
                run.Replay.GetProperty("checkpoints")[i].GetProperty("result").TryGetProperty("events", out JsonElement events) && events.GetArrayLength() > 0);
            JsonElement changed = Change(run.Replay, node => node["checkpoints"]![index]!["result"]!["events"] = new JsonArray());
            ReplayRecord expected = ReplayRecord.Read(changed, Fixtures.Registry());
            HostComparison compared = HostVerification.Compare(expected, ReplayRecord.Read(run.Replay, Fixtures.Registry()));
            Assert.Equal(index, compared.OperationIndex);
            Assert.Contains(compared.Differences, difference => difference.Path.Contains("events"));
            using IHostedGame runtime = StateCodec.Restore(expected.Initial, Fixtures.Environment());
            var prefix = new List<JsonElement>();
            ReplayMismatchException failure = Assert.Throws<ReplayMismatchException>(() => HostVerification.Record(expected.Initial.Module,
                expected.Initial.Configuration, runtime, expected.Commands, Fixtures.Environment(), expected, (checkpoint, _, _) => prefix.Add(checkpoint)));
            Assert.Equal(index, failure.OperationIndex);
            Assert.Equal(index + 2, prefix.Count);
            Assert.Equal(index, prefix.Last().GetProperty("operationIndex").GetInt32());
        }

        [Theory]
        [InlineData("count")]
        [InlineData("index")]
        [InlineData("kind")]
        [InlineData("state")]
        [InlineData("hash")]
        [InlineData("submitted")]
        [InlineData("timing")]
        [InlineData("sequence")]
        [InlineData("events")]
        [InlineData("accepted")]
        [InlineData("error")]
        [InlineData("initial")]
        public void CorruptReplayFailsPrevalidation(string variant)
        {
            RecordedRun run = Fixtures.Record();
            JsonElement changed = Change(run.Replay, node =>
            {
                JsonNode cp = node["checkpoints"]![2]!;
                switch (variant)
                {
                    case "count":
                        node["checkpoints"]!.AsArray().RemoveAt(0);
                        break;
                    case "index":
                        cp["operationIndex"] = 9;
                        break;
                    case "kind":
                        cp["result"]!["kind"] = 8;
                        break;
                    case "state":
                        cp["state"]!["winner"] = "B";
                        break;
                    case "hash":
                        cp["stateHash"] = new string('0', 64);
                        break;
                    case "submitted":
                        cp["runtime"]!["submitted"] = "0";
                        break;
                    case "timing":
                        cp["runtime"]!["ticksPerSecond"] = 60;
                        break;
                    case "sequence":
                        cp["result"]!["inputs"]![0]!["sequence"] = "2";
                        break;
                    case "events":
                        cp["result"]!["events"] = new JsonArray(JsonNode.Parse("{\"type\":\"Unknown\"}"));
                        break;
                    case "accepted":
                        cp["result"]!["inputs"]![0]!["accepted"] = true;
                        break;
                    case "error":
                        cp["result"]!["inputs"]![0]!["error"] = "UnknownError";
                        break;
                    case "initial":
                        node["initialCheckpoint"]!["result"]!["kind"] = 7;
                        break;
                }
            });
            Assert.Throws<ProtocolException>(() => ReplayRecord.Read(changed, Fixtures.Registry()));
        }

        [Theory]
        [InlineData("{\"formatVersion\":1,\"operations\":[]}")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[]}")]
        public void V1IsExplicitlyRejected(string json)
        {
            Assert.Equal("UnsupportedVersion", Assert.Throws<ProtocolException>(() => CommandFile.Read(Fixtures.Json(json), new GridGameModule())).Code);
        }

        [Theory]
        [InlineData("{\"op\":\"unknown\"}")]
        [InlineData("{\"op\":\"advance\",\"extra\":1}")]
        [InlineData("{\"op\":\"submit\",\"command\":{\"type\":\"Move\",\"side\":\"A, B\",\"unitId\":1,\"direction\":\"Right\"}}")]
        [InlineData("{\"op\":\"submit\",\"command\":{\"type\":\"EndTurn\",\"side\":\" A\"}}")]
        [InlineData("{\"op\":\"submit\",\"command\":{\"type\":\"EndTurn\",\"side\":\"A\"}}")]
        public void CommandPrevalidationRejectsInvalidOrUnbalancedSuffix(string operation)
        {
            Assert.Throws<ProtocolException>(() => CommandFile.Read(Fixtures.Json("{\"formatVersion\":2,\"operations\":[{\"op\":\"advance\"}," + operation + "]}"), new GridGameModule()));
        }

        [Fact]
        public void BinaryDivergenceIsLocatedInPhysicsStateAndBounded()
        {
            var expected = Fixtures.Json("{\"state\":{\"physics\":{\"data\":\"" + new string('A', 2000) + "\"}}}");
            var actual = Fixtures.Json("{\"state\":{\"physics\":{\"data\":\"" + new string('B', 2000) + "\"}}}");
            FieldDifference difference = Assert.Single(HostVerification.Differences(expected, actual));
            Assert.Equal("$.state.physics.data", difference.Path);
            Assert.True(difference.Expected.Length < 256);
        }

        [Fact]
        public void FrameworkAssembliesDoNotDependOnExamplesOrUnity()
        {
            foreach (Type type in new[] { typeof(RuntimeState), typeof(IGameModule), typeof(CliRunner), typeof(IPhysicsWorld) })
            {
                Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(), assembly => assembly.Name!.Contains("GridGame")
                    || assembly.Name.Contains("Sandbox") || assembly.Name.Contains("Unity") || assembly.Name.Contains("Examples"));
            }
            Assert.DoesNotContain(typeof(RuntimeState).Assembly.GetReferencedAssemblies(), assembly => assembly.Name!.Contains("Json") || assembly.Name.Contains("Physics"));
            Assert.DoesNotContain(typeof(GridGameModule).Assembly.GetReferencedAssemblies(), assembly => assembly.Name!.Contains("Jolt"));
            Assert.Throws<ArgumentException>(() => new GameRegistry(new GridGameModule(), new GridGameModule()));
        }
    }
}
