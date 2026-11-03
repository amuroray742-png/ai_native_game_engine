using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;

namespace AiNative.GridGame.Hosting
{
    internal static class GridData
    {
        internal static ScenarioDefinition ReadConfiguration(JsonElement element,
            string path, StringComparison sideComparison = StringComparison.Ordinal)
        {
            ProtocolJson.Object(element, path, "width", "height", "obstacles", "units", "actionPointsPerTurn", "attackDamage");
            int width = ProtocolJson.Int32(element.GetProperty("width"), path + ".width");
            int height = ProtocolJson.Int32(element.GetProperty("height"), path + ".height");
            var obstacles = new List<GridPosition>();
            foreach (JsonElement item in ProtocolJson.Array(element.GetProperty("obstacles"), path + ".obstacles"))
            {
                obstacles.Add(ReadPosition(item, $"{path}.obstacles[{obstacles.Count}]"));
            }
            List<UnitDefinition> units = ReadUnits(element.GetProperty("units"), path + ".units", sideComparison);
            int actionPoints = ProtocolJson.Int32(element.GetProperty("actionPointsPerTurn"), path + ".actionPointsPerTurn");
            int attackDamage = ProtocolJson.Int32(element.GetProperty("attackDamage"), path + ".attackDamage");
            return new ScenarioDefinition(width, height, obstacles, units, actionPoints, attackDamage);
        }

        internal static GridGameState ReadGame(JsonElement element, ScenarioDefinition configuration, string path = "$.game")
        {
            ProtocolJson.Object(element, path, "units", "activeSide", "actionPoints", "turnIndex", "winner");
            List<UnitDefinition> units = ReadUnits(element.GetProperty("units"), path + ".units");
            Side activeSide = SideValue(element.GetProperty("activeSide"), path + ".activeSide");
            int actionPoints = ProtocolJson.Int32(element.GetProperty("actionPoints"), path + ".actionPoints");
            long turnIndex = ProtocolJson.Int64(element.GetProperty("turnIndex"), path + ".turnIndex");
            JsonElement winner = element.GetProperty("winner");
            Side? winnerSide = winner.ValueKind == JsonValueKind.Null ? null : SideValue(winner, path + ".winner");
            return new GridGameState(configuration, units, activeSide, actionPoints, turnIndex, winnerSide);
        }

        private static List<UnitDefinition> ReadUnits(JsonElement element, string path,
            StringComparison sideComparison = StringComparison.Ordinal)
        {
            var units = new List<UnitDefinition>();
            foreach (JsonElement item in ProtocolJson.Array(element, path))
            {
                string itemPath = $"{path}[{units.Count}]";
                ProtocolJson.Object(item, itemPath, "id", "side", "position", "health");
                int id = ProtocolJson.Int32(item.GetProperty("id"), itemPath + ".id");
                Side side = SideValue(item.GetProperty("side"), itemPath + ".side", sideComparison);
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

        internal static void WriteConfiguration(Utf8JsonWriter writer, ScenarioDefinition configuration)
        {
            writer.WriteStartObject();
            writer.WriteNumber("width", configuration.Width);
            writer.WriteNumber("height", configuration.Height);
            writer.WriteStartArray("obstacles");
            foreach (GridPosition position in configuration.Obstacles.OrderBy(value => value.Y).ThenBy(value => value.X))
            {
                WritePosition(writer, position);
            }
            writer.WriteEndArray();
            WriteUnits(writer, configuration.Units);
            writer.WriteNumber("actionPointsPerTurn", configuration.ActionPointsPerTurn);
            writer.WriteNumber("attackDamage", configuration.AttackDamage);
            writer.WriteEndObject();
        }

        internal static void WriteGame(Utf8JsonWriter writer, GridGameState game)
        {
            writer.WriteStartObject();
            WriteUnits(writer, game.Units);
            writer.WriteString("activeSide", game.ActiveSide.ToString());
            writer.WriteNumber("actionPoints", game.ActionPoints);
            writer.WriteString("turnIndex", game.TurnIndex.ToString(CultureInfo.InvariantCulture));
            if (game.Winner.HasValue)
            {
                writer.WriteString("winner", game.Winner.Value.ToString());
            }
            else
            {
                writer.WriteNull("winner");
            }
            writer.WriteEndObject();
        }

        private static void WriteUnits(Utf8JsonWriter writer, IReadOnlyList<UnitDefinition> units)
        {
            writer.WriteStartArray("units");
            foreach (UnitDefinition unit in units.OrderBy(value => value.Id))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", unit.Id);
                writer.WriteString("side", unit.Side.ToString());
                writer.WritePropertyName("position");
                WritePosition(writer, unit.Position);
                writer.WriteNumber("health", unit.Health);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private static void WritePosition(Utf8JsonWriter writer, GridPosition position)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", position.X);
            writer.WriteNumber("y", position.Y);
            writer.WriteEndObject();
        }
        internal static GridCommand ReadCommand(JsonElement command, string path)
        {
            if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("type", out JsonElement type))
            {
                throw Invalid(path, "命令必须包含 type。");
            }
            string kind = ProtocolJson.String(type, path + ".type");
            string[] fields = kind switch
            {
                "Move" => new[] { "type", "side", "unitId", "direction" },
                "Attack" => new[] { "type", "side", "unitId", "targetId" },
                "EndTurn" => new[] { "type", "side" },
                _ => throw Invalid(path + ".type", "未知命令类型。")
            };
            ProtocolJson.Object(command, path, fields);
            Side side = SideValue(command.GetProperty("side"), path + ".side");
            if (kind == "EndTurn")
            {
                return new EndTurn(side);
            }
            int unit = ProtocolJson.Int32(command.GetProperty("unitId"), path + ".unitId");
            if (kind == "Attack")
            {
                return new Attack(side, unit, ProtocolJson.Int32(command.GetProperty("targetId"), path + ".targetId"));
            }
            string direction = ProtocolJson.String(command.GetProperty("direction"), path + ".direction");
            MoveDirection moveDirection = direction switch
            {
                "Up" => MoveDirection.Up,
                "Down" => MoveDirection.Down,
                "Left" => MoveDirection.Left,
                "Right" => MoveDirection.Right,
                _ => throw Invalid(path + ".direction", "未知移动方向。")
            };
            return new Move(side, unit, moveDirection);
        }

        private static ProtocolException Invalid(string path, string message) =>
            new ProtocolException("InvalidProtocol", message, path);
        internal static void WriteEvent(Utf8JsonWriter writer, GameEvent value)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case UnitMoved moved:
                    writer.WriteString("type", "UnitMoved");
                    writer.WriteNumber("unitId", moved.UnitId);
                    WritePosition(writer, "from", moved.From);
                    WritePosition(writer, "to", moved.To);
                    break;
                case UnitDamaged damaged:
                    writer.WriteString("type", "UnitDamaged");
                    writer.WriteNumber("unitId", damaged.UnitId);
                    writer.WriteNumber("damage", damaged.Damage);
                    writer.WriteNumber("health", damaged.Health);
                    break;
                case TurnChanged changed:
                    writer.WriteString("type", "TurnChanged");
                    writer.WriteString("side", changed.Side.ToString());
                    writer.WriteString("turnIndex", changed.TurnIndex.ToString(CultureInfo.InvariantCulture));
                    break;
                case GameEnded ended:
                    writer.WriteString("type", "GameEnded");
                    writer.WriteString("winner", ended.Winner.ToString());
                    break;
                default:
                    throw new InvalidOperationException("无法编码未知事件类型。");
            }
            writer.WriteEndObject();
        }

        private static void WritePosition(Utf8JsonWriter writer, string name, GridPosition position)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("x", position.X);
            writer.WriteNumber("y", position.Y);
            writer.WriteEndObject();
        }
        private static Side SideValue(JsonElement element, string path, StringComparison comparison = StringComparison.Ordinal)
        {
            string value = ProtocolJson.String(element, path);
            bool isA = string.Equals(value, "A", comparison);
            JsonData.Require(isA || string.Equals(value, "B", comparison), "InvalidProtocol", path);
            return isA ? Side.A : Side.B;
        }
        private static readonly string[] RuleErrors =
        {
            "GameFinished", "NotYourTurn", "UnknownUnit", "NotYourUnit", "InvalidDirection", "OutOfBounds",
            "Blocked", "Occupied", "NoActionPoints", "UnknownTarget", "FriendlyTarget", "NotAdjacent", "UnknownCommand"
        };

        internal static void ValidateError(string error) => Require(Array.IndexOf(RuleErrors, error) >= 0, "$.error");

        internal static void ReadEvent(JsonElement item, string path)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out JsonElement type))
            {
                throw Invalid(path, "事件缺少 type。");
            }
            switch (ProtocolJson.String(type, path + ".type"))
            {
                case "UnitMoved":
                    ProtocolJson.Object(item, path, "type", "unitId", "from", "to");
                    ProtocolJson.Int32(item.GetProperty("unitId"), path + ".unitId");
                    GridData.ReadPosition(item.GetProperty("from"), path + ".from");
                    GridData.ReadPosition(item.GetProperty("to"), path + ".to");
                    break;
                case "UnitDamaged":
                    ProtocolJson.Object(item, path, "type", "unitId", "damage", "health");
                    ProtocolJson.Int32(item.GetProperty("unitId"), path + ".unitId");
                    Require(ProtocolJson.Int32(item.GetProperty("damage"), path + ".damage") >= 0, path + ".damage");
                    Require(ProtocolJson.Int32(item.GetProperty("health"), path + ".health") >= 0, path + ".health");
                    break;
                case "TurnChanged":
                    ProtocolJson.Object(item, path, "type", "side", "turnIndex");
                    SideValue(item.GetProperty("side"), path + ".side");
                    Require(ProtocolJson.Int64(item.GetProperty("turnIndex"), path + ".turnIndex") > 0, path + ".turnIndex");
                    break;
                case "GameEnded":
                    ProtocolJson.Object(item, path, "type", "winner");
                    SideValue(item.GetProperty("winner"), path + ".winner");
                    break;
                default:
                    throw Invalid(path + ".type", "未知事件类型。");
            }
        }

        internal static void Require(bool condition, string path)
        {
            if (!condition)
            {
                throw Invalid(path, "回放检查点不满足协议或内部一致性约束。");
            }
        }

    }
}
