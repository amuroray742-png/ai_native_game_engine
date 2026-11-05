using System.Collections.Generic;
using System.Text.Json;

namespace AiNative.Hosting
{
    public sealed class CommandOperation
    {
        internal CommandOperation(JsonElement? command)
        {
            Command = command;
        }
        public JsonElement? Command { get; }
    }

    public sealed class CommandFile
    {
        private CommandFile(JsonElement json, IReadOnlyList<CommandOperation> operations)
        {
            Json = json;
            Operations = operations;
        }
        public JsonElement Json { get; }
        public IReadOnlyList<CommandOperation> Operations { get; }

        public static CommandFile Read(JsonElement root, IGameModule module)
        {
            JsonData.Version(root);
            ProtocolJson.Object(root, "$", "formatVersion", "operations");
            var operations = new List<CommandOperation>();
            int pending = 0;
            foreach (JsonElement item in ProtocolJson.Array(root.GetProperty("operations"), "$.operations"))
            {
                string path = "$.operations[" + operations.Count + "]";
                JsonData.Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("op", out _), "InvalidProtocol", path);
                string kind = ProtocolJson.String(item.GetProperty("op"), path + ".op");
                if (kind == "submit")
                {
                    ProtocolJson.Object(item, path, "op", "command");
                    operations.Add(new CommandOperation(module.ReadCommand(item.GetProperty("command"))));
                    pending = checked(pending + 1);
                }
                else
                {
                    JsonData.Require(kind == "advance", "InvalidProtocol", path + ".op");
                    ProtocolJson.Object(item, path, "op");
                    operations.Add(new CommandOperation(null));
                    pending = 0;
                }
            }
            JsonData.Require(pending == 0, "PendingCommands", "$.operations");
            // Re-encode typed commands, so equivalent numeric/case spellings have one representation.
            return new CommandFile(Encode(operations), operations.AsReadOnly());
        }

        internal static JsonElement Encode(IEnumerable<CommandOperation> operations) => JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 2);
                writer.WriteStartArray("operations");
                foreach (CommandOperation operation in operations)
                {
                    writer.WriteStartObject();
                    writer.WriteString("op", operation.Command.HasValue ? "submit" : "advance");
                    if (operation.Command.HasValue)
                    {
                        writer.WritePropertyName("command");
                        operation.Command.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
    }
}
