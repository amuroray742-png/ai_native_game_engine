using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiNative.GridGame;

namespace AiNative.Headless
{
    public static class ScenarioLoader
    {
        /// <summary>文件读取和结构校验仅在宿主发生。领域校验由 GridGameRules.Create 完成。</summary>
        public static ScenarioDefinition Load(string path)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectNullableAnnotations = true,
                AllowDuplicateProperties = false
            };
            options.Converters.Add(new JsonStringEnumConverter<Side>(allowIntegerValues: false));
            using FileStream stream = File.OpenRead(path);
            ScenarioJson data = JsonSerializer.Deserialize<ScenarioJson>(stream, options)
                ?? throw new JsonException("场景根节点不能为 null。");
            if (data.FormatVersion != 1)
            {
                throw new JsonException($"formatVersion={data.FormatVersion} 不受支持，预期为 1。");
            }
            // Nullable 检查不覆盖集合元素，必须明确拒绝 null，不能让映射阶段意外崩溃。
            if (data.Obstacles.Any(position => position == null) || data.Units.Any(unit => unit == null))
            {
                throw new JsonException("obstacles 或 units 的数组元素不能为 null。");
            }
            return new ScenarioDefinition(data.Width, data.Height,
                data.Obstacles.Select(position => new GridPosition(position.X, position.Y)).ToArray(),
                data.Units.Select(unit => new UnitDefinition(unit.Id, unit.Side,
                    new GridPosition(unit.Position.X, unit.Position.Y), unit.Health)).ToArray(),
                data.ActionPointsPerTurn, data.AttackDamage);
        }

        private sealed class ScenarioJson
        {
            public required int FormatVersion
            {
                get; init;
            }
            public required int Width
            {
                get; init;
            }
            public required int Height
            {
                get; init;
            }
            public required PositionJson[] Obstacles
            {
                get; init;
            }
            public required UnitJson[] Units
            {
                get; init;
            }
            public required int ActionPointsPerTurn
            {
                get; init;
            }
            public required int AttackDamage
            {
                get; init;
            }
        }

        private sealed class PositionJson
        {
            public required int X
            {
                get; init;
            }
            public required int Y
            {
                get; init;
            }
        }

        private sealed class UnitJson
        {
            public required int Id
            {
                get; init;
            }
            public required Side Side
            {
                get; init;
            }
            public required PositionJson Position
            {
                get; init;
            }
            public required int Health
            {
                get; init;
            }
        }
    }
}
