using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class SnapshotCodecTests
    {
        [Theory]
        [InlineData("initial.snapshot.json", 0, 1)]
        [InlineData("finished.snapshot.json", 7, 3)]
        public void Decode_FixedSnapshots_PreserveStateAndRoundTrip(string file, long completed, long turn)
        {
            GridGameRuntime restored = SnapshotCodec.Decode(SnapshotFixtures.Read(file), SnapshotFixtures.Environment);
            Assert.Equal(completed, restored.CompletedStepCount);
            Assert.Equal(turn, restored.GetState().TurnIndex);
            byte[] encoded = SnapshotCodec.Encode(restored.ExportState(), SnapshotFixtures.Environment);
            Assert.False(encoded.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.NotEqual((byte)'\n', encoded[^1]);
            GridGameRuntime again = SnapshotCodec.Decode(encoded, SnapshotFixtures.Environment, Fixtures.Definition());
            Assert.Equal(Fixtures.State(restored.GetState()), Fixtures.State(again.GetState()));
            Assert.Equal(42UL, again.RandomState);
        }

        [Theory]
        [InlineData("snapshotFormatVersion", "2", "UnsupportedVersion")]
        [InlineData("rulesId", "\"other\"", "UnsupportedVersion")]
        [InlineData("rulesVersion", "2", "UnsupportedVersion")]
        [InlineData("configFormatVersion", "2", "UnsupportedVersion")]
        [InlineData("configuration.formatVersion", "2", "UnsupportedVersion")]
        [InlineData("digestVersion", "2", "UnsupportedVersion")]
        [InlineData("random.algorithm", "\"unknown\"", "UnsupportedRandomAlgorithm")]
        [InlineData("environment.runtimeVersion", "\"10.0.6\"", "EnvironmentMismatch")]
        [InlineData("environment.os", "\"macos\"", "EnvironmentMismatch")]
        [InlineData("environment.architecture", "\"arm64\"", "EnvironmentMismatch")]
        [InlineData("environment.buildHash", "\"0000000000000000000000000000000000000000000000000000000000000000\"", "EnvironmentMismatch")]
        [InlineData("configuration.width", "0", "InvalidConfiguration")]
        [InlineData("configuration.attackDamage", "3", "ConfigHashMismatch")]
        [InlineData("configHash", "\"0000000000000000000000000000000000000000000000000000000000000000\"", "ConfigHashMismatch")]
        [InlineData("session.pendingCount", "1", "InvalidSnapshot")]
        [InlineData("session.lastSubmittedSequence", "\"1\"", "InvalidSnapshot")]
        [InlineData("game.actionPoints", "3", "InvalidSnapshot")]
        [InlineData("game.turnIndex", "\"0\"", "InvalidSnapshot")]
        [InlineData("game.turnIndex", "\"3\"", "InvalidSnapshot")]
        [InlineData("game.winner", "\"A\"", "InvalidSnapshot")]
        [InlineData("random.state", "\"43\"", "InvalidSnapshot")]
        [InlineData("stateHash", "\"0000000000000000000000000000000000000000000000000000000000000000\"", "InvalidSnapshot")]
        public void Decode_InvalidIdentityOrState_ReportsStableCodeAndPreservesOldSession(string path, string value, string code)
        {
            JsonObject root = SnapshotFixtures.Initial();
            SnapshotFixtures.Set(root, path, JsonNode.Parse(value));
            var old = Fixtures.Session();
            string before = Fixtures.State(old.GetState());
            ProtocolException error = Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                Encoding.UTF8.GetBytes(root.ToJsonString()), SnapshotFixtures.Environment));
            Assert.Equal(code, error.Code);
            Assert.NotNull(error.Path);
            Assert.Equal(before, Fixtures.State(old.GetState()));
            Assert.Equal(42UL, old.RandomState);
            Assert.Equal(1, old.Submit(new EndTurn(Side.A)));
        }

        [Theory]
        [InlineData("game", "null")]
        [InlineData("game.units", "null")]
        [InlineData("game.units", "[null]")]
        [InlineData("game.activeSide", "\"a\"")]
        [InlineData("game.activeSide", "0")]
        [InlineData("game.winner", "\"C\"")]
        [InlineData("configuration.width", "\"5\"")]
        [InlineData("configuration.width", "2147483648")]
        [InlineData("session.completedStepCount", "0")]
        [InlineData("session.completedStepCount", "\"-1\"")]
        [InlineData("session.completedStepCount", "\"9223372036854775808\"")]
        [InlineData("random.state", "\"18446744073709551616\"")]
        [InlineData("random.state", "\"01\"")]
        [InlineData("random.state", "\"+1\"")]
        [InlineData("random.state", "\" 1\"")]
        [InlineData("random.state", "\"1e1\"")]
        [InlineData("random.state", "\"１\"")]
        [InlineData("random.state", "\"\"")]
        [InlineData("stateHash", "\"ABC\"")]
        [InlineData("environment.buildHash", "null")]
        public void Decode_InvalidFieldRepresentation_RejectsWithoutCoercion(string path, string value)
        {
            JsonObject root = SnapshotFixtures.Initial();
            SnapshotFixtures.Set(root, path, JsonNode.Parse(value));
            Assert.Equal("InvalidProtocol", Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                Encoding.UTF8.GetBytes(root.ToJsonString()), SnapshotFixtures.Environment)).Code);
        }

        [Theory]
        [InlineData("duplicate", "InvalidJson")]
        [InlineData("nestedDuplicate", "InvalidJson")]
        [InlineData("escapedDuplicate", "InvalidJson")]
        [InlineData("comment", "InvalidJson")]
        [InlineData("trailingComma", "InvalidJson")]
        [InlineData("broken", "InvalidJson")]
        [InlineData("rootNull", "InvalidProtocol")]
        [InlineData("unknown", "InvalidProtocol")]
        [InlineData("nestedUnknown", "InvalidProtocol")]
        [InlineData("exponent", "InvalidProtocol")]
        [InlineData("decimal", "InvalidProtocol")]
        public void Decode_InvalidJsonStructure_IsRejected(string variant, string code)
        {
            string json = Encoding.UTF8.GetString(SnapshotFixtures.Read("initial.snapshot.json"));
            json = variant switch
            {
                "duplicate" => json.Insert(1, "\"rulesVersion\":1,"),
                "escapedDuplicate" => json.Insert(1, "\"rules\\u0056ersion\":1,"),
                "nestedDuplicate" => json.Replace("\"width\": 5", "\"width\": 5,\"width\":5"),
                "comment" => json.Insert(1, "/* comment */"),
                "trailingComma" => json.TrimEnd().Insert(json.TrimEnd().Length - 1, ","),
                "broken" => "{",
                "rootNull" => "null",
                "unknown" => json.Insert(1, "\"unexpected\":1,"),
                "nestedUnknown" => json.Replace("\"width\": 5", "\"unexpected\":1,\"width\":5"),
                "exponent" => json.Replace("\"width\": 5", "\"width\": 5e0"),
                "decimal" => json.Replace("\"width\": 5", "\"width\": 5.0"),
                _ => throw new ArgumentException(variant)
            };
            Assert.Equal(code, Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                Encoding.UTF8.GetBytes(json), SnapshotFixtures.Environment)).Code);
        }

        [Fact]
        public void Decode_MissingWinner_IsRejected()
        {
            JsonObject root = SnapshotFixtures.Initial();
            JsonObject game = Assert.IsType<JsonObject>(root["game"]);
            Assert.True(game.Remove("winner"));

            ProtocolException error = Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                Encoding.UTF8.GetBytes(root.ToJsonString()), SnapshotFixtures.Environment));
            Assert.Equal("InvalidProtocol", error.Code);
            Assert.Equal("$.game.winner", error.Path);
        }

        [Theory]
        [InlineData("value", "\\uD800")]
        [InlineData("property", "\\uD800")]
        [InlineData("value", "\\uDC00")]
        [InlineData("property", "\\uDC00")]
        public void Decode_UnpairedUnicodeEscape_IsReportedAsJsonFailure(string location, string escape)
        {
            string json = Encoding.UTF8.GetString(SnapshotFixtures.Read("initial.snapshot.json"));
            json = location == "value"
                ? json.Replace("0.0.0", escape)
                : json.Insert(1, "\"" + escape + "\":1,");
            Assert.Equal("InvalidJson", Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                Encoding.UTF8.GetBytes(json), SnapshotFixtures.Environment)).Code);
        }

        [Fact]
        public void Decode_InvalidUtf8_IsReportedAsJsonFailure()
        {
            Assert.Equal("InvalidJson", Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                new byte[] { 123, 34, 0xFF, 34, 58, 48, 125 }, SnapshotFixtures.Environment)).Code);
        }

        [Fact]
        public void Decode_ErrorPriorityAndExpectedConfiguration_AreExplicit()
        {
            JsonObject root = SnapshotFixtures.Initial();
            root["snapshotFormatVersion"] = 2;
            root["game"]!["activeSide"] = "bad";
            Assert.Equal("InvalidProtocol", Failure(root, SnapshotEnvironment.Current()).Code);
            root["game"]!["activeSide"] = "A";
            Assert.Equal("UnsupportedVersion", Failure(root, SnapshotEnvironment.Current()).Code);
            root["snapshotFormatVersion"] = 1;
            root["configuration"]!["width"] = 0;
            Assert.Equal("EnvironmentMismatch", Failure(root, SnapshotEnvironment.Current()).Code);
            ScenarioDefinition source = Fixtures.Definition();
            var changed = new ScenarioDefinition(5, 5, source.Obstacles, source.Units, 2, 3);
            Assert.Equal("ExpectedConfigMismatch", Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(
                SnapshotFixtures.Read("initial.snapshot.json"), SnapshotFixtures.Environment, changed)).Code);
        }

        [Fact]
        public void Decode_LargeIntegersAndReorderedInput_RemainExact()
        {
            GridGameState state = GridGameRules.Create(Fixtures.Definition()).ExportState();
            var session = new SessionState(long.MaxValue, long.MaxValue, DeterministicRandom.Algorithm, ulong.MaxValue);
            JsonObject root = JsonNode.Parse(SnapshotCodec.Encode(new GridGameSnapshot(session, state), SnapshotFixtures.Environment))!.AsObject();
            // 调度计数不参与玩法摘要；随机状态参与，因此极值随机与初始摘要不同。
            Assert.NotEqual(SnapshotFixtures.Initial()["stateHash"]!.GetValue<string>(), root["stateHash"]!.GetValue<string>());
            JsonArray units = root["game"]!["units"]!.AsArray();
            JsonNode? first = units[0];
            units.RemoveAt(0);
            units.Add(first);
            JsonObject reordered = new JsonObject(root.Reverse().Select(item =>
                new System.Collections.Generic.KeyValuePair<string, JsonNode?>(item.Key, item.Value?.DeepClone())));
            GridGameRuntime restored = SnapshotCodec.Decode(Encoding.UTF8.GetBytes(reordered.ToJsonString()), SnapshotFixtures.Environment);
            Assert.Equal(long.MaxValue, restored.CompletedStepCount);
            Assert.Equal(ulong.MaxValue, restored.RandomState);
            Assert.Throws<OverflowException>(() => restored.Submit(new EndTurn(Side.A)));
            Assert.False(restored.IsFaulted);
        }

        private static ProtocolException Failure(JsonObject root, HostEnvironment environment) =>
            Assert.Throws<ProtocolException>(() => SnapshotCodec.Decode(Encoding.UTF8.GetBytes(root.ToJsonString()), environment));
    }
}
