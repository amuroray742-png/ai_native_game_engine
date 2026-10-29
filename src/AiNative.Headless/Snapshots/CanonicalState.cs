using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Headless
{
    /// <summary>显式写入协议字段顺序；不依赖反射、文化区域或默认属性排序。</summary>
    internal static class CanonicalState
    {
        internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        internal static byte[] ConfigurationBytes(ScenarioDefinition configuration) =>
            WriteBytes(writer => WriteConfiguration(writer, configuration));

        internal static byte[] StateBytes(GridGameState game, string algorithm, ulong randomState) =>
            WriteBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("digestVersion", 1);
                writer.WriteString("rulesId", "grid-game");
                writer.WriteNumber("rulesVersion", 1);
                writer.WriteString("configHash", Hash(ConfigurationBytes(game.Configuration)));
                writer.WritePropertyName("random");
                WriteRandom(writer, algorithm, randomState);
                writer.WritePropertyName("game");
                WriteGame(writer, game);
                writer.WriteEndObject();
            });

        internal static byte[] WriteBytes(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                write(writer);
            }
            return stream.ToArray();
        }

        internal static void WriteConfiguration(Utf8JsonWriter writer, ScenarioDefinition configuration)
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
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

        internal static void WriteRandom(Utf8JsonWriter writer, string algorithm, ulong state)
        {
            writer.WriteStartObject();
            writer.WriteString("algorithm", algorithm);
            writer.WriteString("state", state.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        internal static void WriteSession(Utf8JsonWriter writer, SessionState session)
        {
            writer.WriteStartObject();
            writer.WriteString("lastSubmittedSequence", session.LastSubmittedSequence.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("completedStepCount", session.CompletedStepCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("pendingCount", 0);
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
    }
}
