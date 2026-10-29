using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CommandFileTests
    {
        [Theory]
        [InlineData("{}", "InvalidProtocol")]
        [InlineData("null", "InvalidProtocol")]
        [InlineData("{", "InvalidJson")]
        [InlineData("{\"commandFormatVersion\":1,\"commandFormatVersion\":1,\"operations\":[]}", "InvalidJson")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[],\"extra\":1}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":null}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[null]}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1.0,\"operations\":[]}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[{\"op\":\"advance\",\"command\":null}]}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[{\"op\":\"submit\"}]}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[{\"op\":\"Advance\"}]}", "InvalidProtocol")]
        [InlineData("{\"commandFormatVersion\":1,\"operations\":[{\"op\":\"\\uD800\"}]}", "InvalidJson")]
        public void InvalidFile_IsRejectedBeforeAnyOperation(string json, string expected)
        {
            using var directory = new SnapshotDirectory();
            string path = CliTests.WriteCommands(directory, Encoding.UTF8.GetBytes(json));
            var call = CliTests.Invoke(CliTests.RunArgs(path));
            Assert.Equal(2, call.Code);
            Assert.Equal(expected, call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
            Assert.Empty(call.Result["checkpoints"]!.AsArray());
        }

        [Theory]
        [InlineData("null")]
        [InlineData("{\"type\":\"Teleport\",\"side\":\"A\"}")]
        [InlineData("{\"type\":\"EndTurn\",\"side\":\"a\"}")]
        [InlineData("{\"type\":\"EndTurn\",\"side\":0}")]
        [InlineData("{\"type\":\"EndTurn\",\"side\":\"A\",\"unitId\":1}")]
        [InlineData("{\"type\":\"Attack\",\"side\":\"A\",\"unitId\":1}")]
        [InlineData("{\"type\":\"Attack\",\"side\":\"A\",\"unitId\":2147483648,\"targetId\":2}")]
        [InlineData("{\"type\":\"Move\",\"side\":\"A\",\"unitId\":1,\"direction\":\"up\"}")]
        [InlineData("{\"type\":\"Move\",\"side\":\"A\",\"unitId\":\"1\",\"direction\":\"Up\"}")]
        [InlineData("{\"type\":\"Move\",\"side\":\"A\",\"unitId\":1e0,\"direction\":\"Up\"}")]
        [InlineData("{\"type\":\"Move\",\"side\":\"A\",\"unitId\":1,\"direction\":0}")]
        [InlineData("{\"type\":\"Move\",\"side\":\"A\",\"unitId\":1,\"to\":{\"x\":1,\"y\":0}}")]
        public void InvalidCommand_LaterInFile_DoesNotExecuteValidPrefix(string command)
        {
            using var directory = new SnapshotDirectory();
            string json = "{\"commandFormatVersion\":1,\"operations\":[{\"op\":\"advance\"},{\"op\":\"submit\",\"command\":" + command + "},{\"op\":\"advance\"}]}";
            string path = CliTests.WriteCommands(directory, Encoding.UTF8.GetBytes(json));
            var call = CliTests.Invoke(CliTests.RunArgs(path));
            Assert.Equal("InvalidProtocol", call.Result["code"]!.GetValue<string>());
            Assert.Equal(1, call.Result["location"]!["operationIndex"]!.GetValue<int>());
            Assert.Null(call.Result["initialCheckpoint"]);
            Assert.Empty(call.Result["checkpoints"]!.AsArray());
        }

        [Theory]
        [InlineData("A,B")]
        [InlineData("A, B")]
        [InlineData(" A ")]
        public void Scenario_CompositeOrPaddedSide_IsNotAcceptedByEnumCoercion(string side)
        {
            using var directory = new SnapshotDirectory();
            JsonObject scene = JsonNode.Parse(SnapshotFixtures.Read("scenario.json"))!.AsObject();
            scene["units"]![1]!["side"] = side;
            string path = Path.Combine(directory.Path, "scenario.json");
            File.WriteAllText(path, scene.ToJsonString());
            string commands = CliTests.WriteCommands(directory, SnapshotFixtures.Read("empty-commands.json"));
            var call = CliTests.Invoke(new[] { "run", "--scenario", path, "--seed", "42", "--commands", commands });
            Assert.Equal(2, call.Code);
            Assert.Equal("InvalidProtocol", call.Result["code"]!.GetValue<string>());
            Assert.Null(call.Result["initialCheckpoint"]);
        }

        [Fact]
        public void InputPriority_ReadAllThenStructureThenVersionsThenDomain()
        {
            using var directory = new SnapshotDirectory();
            string scene = Path.Combine(directory.Path, "scenario.json");
            File.WriteAllText(scene, "{");
            string missing = Path.Combine(directory.Path, "missing.json");
            string[] args = { "run", "--scenario", scene, "--seed", "42", "--commands", missing };
            Assert.Equal("InputReadFailed", CliTests.Invoke(args).Result["code"]!.GetValue<string>());
            File.WriteAllText(missing, "{\"commandFormatVersion\":2,\"operations\":[]}");
            Assert.Equal("InvalidJson", CliTests.Invoke(args).Result["code"]!.GetValue<string>());
            JsonObject config = JsonNode.Parse(SnapshotFixtures.Read("scenario.json"))!.AsObject();
            config["width"] = 0;
            File.WriteAllText(scene, config.ToJsonString());
            Assert.Equal("UnsupportedVersion", CliTests.Invoke(args).Result["code"]!.GetValue<string>());
            File.WriteAllBytes(missing, SnapshotFixtures.Read("empty-commands.json"));
            Assert.Equal("InvalidConfiguration", CliTests.Invoke(args).Result["code"]!.GetValue<string>());
            config["width"] = 5;
            config["units"]![0]!["side"] = "a";
            File.WriteAllText(scene, config.ToJsonString());
            Assert.Equal(0, CliTests.Invoke(args).Code);
        }
    }
}
