using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.GridGame;

namespace AiNative.Headless
{
    internal static class SnapshotReader
    {
        internal static SnapshotData Read(ReadOnlyMemory<byte> bytes)
        {
            using JsonDocument document = JsonInput.Parse(bytes);
            return Read(document.RootElement);
        }

        internal static SnapshotData Read(JsonElement root)
        {
            ProtocolJson.Object(root, "$", "snapshotFormatVersion", "rulesId", "rulesVersion", "configFormatVersion",
                "configuration", "configHash", "session", "random", "game", "digestVersion", "stateHash", "environment");
            int format = ProtocolJson.Int32(root.GetProperty("snapshotFormatVersion"), "$.snapshotFormatVersion");
            string rulesId = ProtocolJson.String(root.GetProperty("rulesId"), "$.rulesId");
            int rulesVersion = ProtocolJson.Int32(root.GetProperty("rulesVersion"), "$.rulesVersion");
            int configVersion = ProtocolJson.Int32(root.GetProperty("configFormatVersion"), "$.configFormatVersion");
            (int sceneVersion, ScenarioDefinition configuration) = ReadConfiguration(root.GetProperty("configuration"));
            string configHash = ProtocolJson.Hash(root.GetProperty("configHash"), "$.configHash");
            JsonElement session = root.GetProperty("session");
            ProtocolJson.Object(session, "$.session", "lastSubmittedSequence", "completedStepCount", "pendingCount");
            long submitted = ProtocolJson.Int64(session.GetProperty("lastSubmittedSequence"), "$.session.lastSubmittedSequence");
            long completed = ProtocolJson.Int64(session.GetProperty("completedStepCount"), "$.session.completedStepCount");
            int pending = ProtocolJson.Int32(session.GetProperty("pendingCount"), "$.session.pendingCount");
            JsonElement random = root.GetProperty("random");
            ProtocolJson.Object(random, "$.random", "algorithm", "state");
            string algorithm = ProtocolJson.String(random.GetProperty("algorithm"), "$.random.algorithm");
            ulong randomState = ProtocolJson.UInt64(random.GetProperty("state"), "$.random.state");
            GridGameState game = ReadGame(root.GetProperty("game"), configuration);
            int digestVersion = ProtocolJson.Int32(root.GetProperty("digestVersion"), "$.digestVersion");
            string stateHash = ProtocolJson.Hash(root.GetProperty("stateHash"), "$.stateHash");
            SnapshotEnvironment environment = ReadEnvironment(root.GetProperty("environment"));
            return new SnapshotData(format, rulesId, rulesVersion, configVersion, sceneVersion, configHash,
                submitted, completed, pending, algorithm, randomState, game, digestVersion, stateHash, environment);
        }

        private static (int Version, ScenarioDefinition Configuration) ReadConfiguration(JsonElement element)
        {
            const string path = "$.configuration";
            ProtocolJson.Object(element, path, "formatVersion", "width", "height", "obstacles", "units", "actionPointsPerTurn", "attackDamage");
            int version = ProtocolJson.Int32(element.GetProperty("formatVersion"), path + ".formatVersion");
            int width = ProtocolJson.Int32(element.GetProperty("width"), path + ".width");
            int height = ProtocolJson.Int32(element.GetProperty("height"), path + ".height");
            var obstacles = new List<GridPosition>();
            foreach (JsonElement item in ProtocolJson.Array(element.GetProperty("obstacles"), path + ".obstacles"))
            {
                obstacles.Add(ReadPosition(item, $"{path}.obstacles[{obstacles.Count}]"));
            }
            List<UnitDefinition> units = ReadUnits(element.GetProperty("units"), path + ".units");
            int actionPoints = ProtocolJson.Int32(element.GetProperty("actionPointsPerTurn"), path + ".actionPointsPerTurn");
            int attackDamage = ProtocolJson.Int32(element.GetProperty("attackDamage"), path + ".attackDamage");
            return (version, new ScenarioDefinition(width, height, obstacles, units, actionPoints, attackDamage));
        }

        internal static GridGameState ReadGame(JsonElement element, ScenarioDefinition configuration, string path = "$.game")
        {
            ProtocolJson.Object(element, path, "units", "activeSide", "actionPoints", "turnIndex", "winner");
            List<UnitDefinition> units = ReadUnits(element.GetProperty("units"), path + ".units");
            Side activeSide = ProtocolJson.SideValue(element.GetProperty("activeSide"), path + ".activeSide");
            int actionPoints = ProtocolJson.Int32(element.GetProperty("actionPoints"), path + ".actionPoints");
            long turnIndex = ProtocolJson.Int64(element.GetProperty("turnIndex"), path + ".turnIndex");
            JsonElement winner = element.GetProperty("winner");
            Side? winnerSide = winner.ValueKind == JsonValueKind.Null ? null : ProtocolJson.SideValue(winner, path + ".winner");
            return new GridGameState(configuration, units, activeSide, actionPoints, turnIndex, winnerSide);
        }

        private static List<UnitDefinition> ReadUnits(JsonElement element, string path)
        {
            var units = new List<UnitDefinition>();
            foreach (JsonElement item in ProtocolJson.Array(element, path))
            {
                string itemPath = $"{path}[{units.Count}]";
                ProtocolJson.Object(item, itemPath, "id", "side", "position", "health");
                int id = ProtocolJson.Int32(item.GetProperty("id"), itemPath + ".id");
                Side side = ProtocolJson.SideValue(item.GetProperty("side"), itemPath + ".side");
                GridPosition position = ReadPosition(item.GetProperty("position"), itemPath + ".position");
                int health = ProtocolJson.Int32(item.GetProperty("health"), itemPath + ".health");
                units.Add(new UnitDefinition(id, side, position, health));
            }
            return units;
        }

        internal static GridPosition ReadPosition(JsonElement element, string path)
        {
            ProtocolJson.Object(element, path, "x", "y");
            return new GridPosition(ProtocolJson.Int32(element.GetProperty("x"), path + ".x"),
                ProtocolJson.Int32(element.GetProperty("y"), path + ".y"));
        }

        private static SnapshotEnvironment ReadEnvironment(JsonElement element)
        {
            const string path = "$.environment";
            ProtocolJson.Object(element, path, "runtimeVersion", "os", "architecture", "buildHash");
            return new SnapshotEnvironment(
                ProtocolJson.String(element.GetProperty("runtimeVersion"), path + ".runtimeVersion"),
                ProtocolJson.String(element.GetProperty("os"), path + ".os"),
                ProtocolJson.String(element.GetProperty("architecture"), path + ".architecture"),
                ProtocolJson.Hash(element.GetProperty("buildHash"), path + ".buildHash"));
        }
    }

    // 仅在宿主内传递已完成结构读取、尚待兼容性及领域校验的数据。
    internal sealed record SnapshotData(int FormatVersion, string RulesId, int RulesVersion,
        int ConfigFormatVersion, int SceneFormatVersion, string ConfigHash,
        long Submitted, long Completed, int Pending, string Algorithm, ulong RandomState,
        GridGameState Game, int DigestVersion, string StateHash, SnapshotEnvironment Environment);
}
