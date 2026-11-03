using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.GridGame;

namespace AiNative.GridGame.Hosting
{
    public sealed class CommandOperation
    {
        internal CommandOperation(GridCommand? command)
        {
            Command = command;
        }

        /// <summary>非 null 表示提交该命令；null 表示显式推进一次。</summary>
        public GridCommand? Command { get; }
    }
    public sealed class CommandFile
    {
        internal CommandFile(int version, IReadOnlyList<CommandOperation> operations)
        {
            Version = version;
            Operations = operations;
        }

        public int Version { get; }
        public IReadOnlyList<CommandOperation> Operations { get; }

        /// <summary>读取完整 v1 操作序列并校验队列平衡；不执行规则，非法输入抛 ProtocolException。</summary>
        public static CommandFile Decode(ReadOnlyMemory<byte> bytes)
        {
            CommandFile file = CommandFileReader.Read(bytes);
            if (file.Version != 1)
            {
                throw new ProtocolException("UnsupportedVersion", "不支持的命令文件版本。", "$.commandFormatVersion");
            }
            CommandFileReader.ValidateBalance(file);
            return file;
        }
    }

    internal static class CommandFileReader
    {
        internal static CommandFile Read(ReadOnlyMemory<byte> bytes)
        {
            using JsonDocument document = JsonDocumentReader.Parse(bytes);
            return Read(document.RootElement);
        }

        internal static CommandFile Read(JsonElement root)
        {
            ProtocolJson.Object(root, "$", "commandFormatVersion", "operations");
            int version = ProtocolJson.Int32(root.GetProperty("commandFormatVersion"), "$.commandFormatVersion");
            var operations = new List<CommandOperation>();
            foreach (JsonElement item in ProtocolJson.Array(root.GetProperty("operations"), "$.operations"))
            {
                string path = $"$.operations[{operations.Count}]";
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("op", out JsonElement op))
                {
                    throw Invalid(path, "操作必须包含 op。");
                }
                string kind = ProtocolJson.String(op, path + ".op");
                if (kind == "advance")
                {
                    ProtocolJson.Object(item, path, "op");
                    operations.Add(new CommandOperation(null));
                }
                else if (kind == "submit")
                {
                    ProtocolJson.Object(item, path, "op", "command");
                    operations.Add(new CommandOperation(ReadCommand(item.GetProperty("command"), path + ".command")));
                }
                else
                {
                    throw Invalid(path + ".op", "未知操作。");
                }
            }
            return new CommandFile(version, operations.AsReadOnly());
        }

        internal static void ValidateBalance(CommandFile file, string path = "$.operations")
        {
            int pending = 0;
            foreach (CommandOperation operation in file.Operations)
            {
                if (operation.Command != null)
                {
                    pending++;
                }
                else if (pending > 0)
                {
                    pending--;
                }
            }
            if (pending != 0)
            {
                throw new ProtocolException("PendingCommands", "命令文件结束时仍有待执行命令。", path);
            }
        }

        private static GridCommand ReadCommand(JsonElement command, string path)
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
            Side side = ProtocolJson.SideValue(command.GetProperty("side"), path + ".side");
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
    }
}
