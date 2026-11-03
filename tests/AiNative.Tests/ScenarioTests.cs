using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.GridGame;
using AiNative.Hosting;
using Xunit;

namespace AiNative.Tests
{
    public sealed class ScenarioTests
    {
        [Theory]
        [InlineData("width", 0)]
        [InlineData("height", -1)]
        [InlineData("attackDamage", 0)]
        [InlineData("actionPointsPerTurn", 0)]
        [InlineData("units[0].health", 0)]
        [InlineData("units[0].id", 0)]
        [InlineData("units[1].id", 1)]
        [InlineData("units[1].side", "A")]
        [InlineData("units[0].position.x", -1)]
        [InlineData("units[1].position.x", 0)]
        public void Create_WhenDomainConfigurationInvalid_ReportsReason(string field, object value)
        {
            JsonNode root = JsonNode.Parse(Fixtures.Configuration.GetRawText()) ?? throw new Exception();
            string[] parts = field.Replace("[", ".").Replace("]", "").Split('.');
            JsonNode parent = root;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                parent = (int.TryParse(parts[index], out int item) ? parent[item] : parent[parts[index]]) ?? throw new Exception();
            }
            parent[parts[parts.Length - 1]] = JsonSerializer.SerializeToNode(value);
            WithJson(root.ToJsonString(), path =>
            {
                ProtocolException error = Assert.Throws<ProtocolException>(() => Fixtures.LoadConfiguration(path));
                Assert.False(string.IsNullOrWhiteSpace(error.Message));
            });
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("null")]
        [InlineData("{ invalid")]
        public void Load_WhenRootInvalid_ReportsJsonError(string json)
        {
            WithJson(json, path => Assert.Throws<ProtocolException>(() => Fixtures.LoadConfiguration(path)));
        }

        [Theory]
        [InlineData("version")]
        [InlineData("missing")]
        [InlineData("unknown")]
        [InlineData("nestedUnknown")]
        [InlineData("nullUnits")]
        [InlineData("nullElement")]
        [InlineData("nullPosition")]
        [InlineData("badSide")]
        [InlineData("numericSide")]
        [InlineData("wrongType")]
        [InlineData("duplicate")]
        public void Load_WhenStructureInvalid_RejectsWithoutDefaults(string variant)
        {
            JsonNode root = JsonNode.Parse(Fixtures.Configuration.GetRawText()) ?? throw new Exception();
            switch (variant)
            {
                case "version":
                    root["formatVersion"] = 2;
                    break;
                case "missing":
                    root.AsObject().Remove("attackDamage");
                    break;
                case "unknown":
                    root["typo"] = 2;
                    break;
                case "nestedUnknown":
                    root["units"]![0]!["typo"] = 2;
                    break;
                case "nullUnits":
                    root["units"] = null;
                    break;
                case "nullElement":
                    root["obstacles"]![0] = null;
                    break;
                case "nullPosition":
                    root["units"]![0]!["position"] = null;
                    break;
                case "badSide":
                    root["units"]![0]!["side"] = "C";
                    break;
                case "numericSide":
                    root["units"]![0]!["side"] = 0;
                    break;
                case "wrongType":
                    root["width"] = "five";
                    break;
            }
            string json = root.ToJsonString();
            if (variant == "duplicate")
            {
                json = json.Insert(1, "\"width\":5,");
            }
            WithJson(json, path => Assert.Throws<ProtocolException>(() => Fixtures.LoadConfiguration(path)));
        }

        [Fact]
        public void Create_RejectsInvalidCollectionsAndSides()
        {
            ScenarioDefinition source = Fixtures.Definition();
            Assert.Throws<ArgumentException>(() => GridGameRules.Create(new ScenarioDefinition(5, 5,
                new[] { new GridPosition(2, 2), new GridPosition(2, 2) }, source.Units, 2, 2)));
            Assert.Throws<ArgumentException>(() => GridGameRules.Create(new ScenarioDefinition(5, 5,
                new[] { new GridPosition(0, 0) }, source.Units, 2, 2)));
            Assert.Throws<ArgumentException>(() => GridGameRules.Create(new ScenarioDefinition(5, 5,
                new[] { new GridPosition(5, 5) }, source.Units, 2, 2)));
            Assert.Throws<ArgumentException>(() => GridGameRules.Create(new ScenarioDefinition(5, 5,
                source.Obstacles, new[] { source.Units[0] }, 2, 2)));
            Assert.Throws<ArgumentException>(() => GridGameRules.Create(new ScenarioDefinition(5, 5,
                source.Obstacles, new[] { new UnitDefinition(1, (Side)5, new GridPosition(0, 0), 4), source.Units[1] }, 2, 2)));
        }

        internal static void WithJson(string json, Action<string> action)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);
                action(path);
            }
            finally { File.Delete(path); }
        }
    }
}
