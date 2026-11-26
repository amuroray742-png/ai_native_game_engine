using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AiNative.Multiplayer.Protocol.Messages;

namespace AiNative.Multiplayer.Protocol.Serialization
{
    /// <summary>v1 联机 JSON 正文边界；不包含 TCP 帧头，不认证身份，不裁定游戏规则。</summary>
    public static class MultiplayerCodec
    {
        public const int Version = 1;
        public const int MaximumBytes = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryDecode(ReadOnlyMemory<byte> bytes, MessageDirection direction,
            out MultiplayerMessage? message, out MessageError error)
        {
            message = null;
            error = MessageError.InvalidMessage;
            if ((direction != MessageDirection.ClientToServer && direction != MessageDirection.ServerToClient) || bytes.Length == 0 || bytes.Length > MaximumBytes) { return false; }
            try
            {
                StrictUtf8.GetCharCount(bytes.Span);
                using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                if (!JsonFields.Unique(root) || !JsonFields.Exact(root, "protocolVersion", "type", "requestId", "payload")) { return false; }
                JsonElement version = root.GetProperty("protocolVersion");
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number)) { return false; }
                if (number != Version)
                {
                    error = MessageError.UnsupportedProtocolVersion;
                    return false;
                }
                if (!JsonFields.Text(root.GetProperty("type"))) { return false; }
                string type = root.GetProperty("type").GetString()!;
                JsonElement requestId = root.GetProperty("requestId");
                JsonElement payload = root.GetProperty("payload");
                if (!ValidDirection(type, direction) || !ValidRequestId(type, direction, requestId) || !ValidPayload(type, payload)) { return false; }
                message = new MultiplayerMessage(type, requestId.GetString(), payload);
                error = MessageError.None;
                return true;
            }
            catch (JsonException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        /// <summary>编码前执行同一结构校验；无效的本地消息抛 ArgumentException。</summary>
        public static byte[] Encode(string type, string? requestId, JsonElement payload, MessageDirection direction)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("protocolVersion", Version);
                writer.WriteString("type", type);
                writer.WriteString("requestId", requestId);
                writer.WritePropertyName("payload");
                payload.WriteTo(writer);
                writer.WriteEndObject();
            }
            byte[] bytes = stream.ToArray();
            if (!TryDecode(bytes, direction, out _, out _)) { throw new ArgumentException("联机消息不符合 v1 契约。"); }
            return bytes;
        }

        private static bool ValidDirection(string type, MessageDirection direction)
        {
            if (type == "Ping" || type == "Pong") { return true; }
            if (direction == MessageDirection.ClientToServer)
            {
                return type == "Hello" || type == "CreateRoom" || type == "JoinRoom" || type == "ResumeRoom" ||
                    type == "GetRoom" || type == "LeaveRoom" || type == "SubmitCommand";
            }
            if (direction != MessageDirection.ServerToClient) { return false; }
            return type == "Welcome" || type == "RoomSnapshot" || type == "RoomLeft" || type == "CommandResult" ||
                type == "StateUpdate" || type == "RoomStatus" || type == "ServiceStatus" || type == "Error";
        }

        private static bool ValidRequestId(string type, MessageDirection direction, JsonElement value)
        {
            bool push = type == "StateUpdate" || type == "RoomStatus" || type == "ServiceStatus";
            if (push) { return value.ValueKind == JsonValueKind.Null; }
            if (value.ValueKind == JsonValueKind.Null)
            {
                return direction == MessageDirection.ServerToClient && (type == "RoomSnapshot" || type == "Error" || type == "Ping" || type == "Pong");
            }
            if (!JsonFields.Text(value, 64)) { return false; }
            foreach (char character in value.GetString()!)
            {
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') && character != '-') { return false; }
            }
            return true;
        }

        private static bool ValidPayload(string type, JsonElement payload)
        {
            switch (type)
            {
                case "Hello":
                    return JsonFields.Exact(payload, "resumeToken") && JsonFields.Nullable(payload.GetProperty("resumeToken"), Token);
                case "Welcome":
                    return JsonFields.Exact(payload, "controlEpoch", "playerId", "resumeToken", "connectionGeneration", "activeMatch") &&
                        JsonFields.Uuid(payload.GetProperty("controlEpoch")) && JsonFields.Uuid(payload.GetProperty("playerId")) &&
                        Token(payload.GetProperty("resumeToken")) && JsonFields.Counter(payload.GetProperty("connectionGeneration")) &&
                        JsonFields.Nullable(payload.GetProperty("activeMatch"), JsonFields.Match);
                case "CreateRoom":
                    return JsonFields.Exact(payload, "gameId", "gameVersion") && JsonFields.Text(payload.GetProperty("gameId")) && JsonFields.Text(payload.GetProperty("gameVersion"));
                case "JoinRoom": case "ResumeRoom": case "GetRoom": case "LeaveRoom": case "RoomLeft":
                    return JsonFields.Exact(payload, "match") && JsonFields.Match(payload.GetProperty("match"));
                case "SubmitCommand":
                    return JsonFields.Exact(payload, "match", "requestSequence", "expectedTick", "command") &&
                        JsonFields.Match(payload.GetProperty("match")) && JsonFields.Counter(payload.GetProperty("requestSequence")) &&
                        JsonFields.Counter(payload.GetProperty("expectedTick")) && JsonFields.Object(payload.GetProperty("command"));
                case "Ping": case "Pong":
                    return JsonFields.Exact(payload, "nonce") && JsonFields.Counter(payload.GetProperty("nonce"));
                case "Error":
                    bool fields = JsonFields.Exact(payload, "code", "match") || JsonFields.Exact(payload, "code", "match", "message");
                    return fields && JsonFields.Text(payload.GetProperty("code")) && JsonFields.Nullable(payload.GetProperty("match"), JsonFields.Match) &&
                        (!payload.TryGetProperty("message", out JsonElement diagnostic) || JsonFields.Text(diagnostic, 1024));
                case "ServiceStatus":
                    return JsonFields.Exact(payload, "code", "match") && JsonFields.Text(payload.GetProperty("code")) && JsonFields.Nullable(payload.GetProperty("match"), JsonFields.Match);
                case "CommandResult": return CommandResult(payload);
                case "RoomStatus":
                    return JsonFields.Exact(payload, "match", "revision", "status", "players", "endReason") &&
                        RoomIdentity(payload) && Players(payload.GetProperty("players"));
                case "RoomSnapshot":
                    return JsonFields.Exact(payload, "match", "revision", "status", "seat", "players", "configuration", "tick", "observation", "lastRequestSequence", "lastCommandResult", "endReason") &&
                        RoomIdentity(payload) && JsonFields.Seat(payload.GetProperty("seat")) && Players(payload.GetProperty("players")) &&
                        JsonFields.Object(payload.GetProperty("configuration")) && JsonFields.Nullable(payload.GetProperty("tick"), JsonFields.Counter) &&
                        JsonFields.Nullable(payload.GetProperty("observation"), JsonFields.Object) && JsonFields.Counter(payload.GetProperty("lastRequestSequence")) &&
                        JsonFields.Nullable(payload.GetProperty("lastCommandResult"), CommandResult);
                case "StateUpdate":
                    return JsonFields.Exact(payload, "match", "revision", "status", "tick", "inputSequence", "actorSeat", "requestSequence", "accepted", "ruleError", "events", "observation", "endReason") &&
                        RoomIdentity(payload) && JsonFields.Counter(payload.GetProperty("tick")) && JsonFields.Counter(payload.GetProperty("inputSequence")) &&
                        JsonFields.Seat(payload.GetProperty("actorSeat")) && JsonFields.Counter(payload.GetProperty("requestSequence")) &&
                        JsonFields.Boolean(payload.GetProperty("accepted")) && JsonFields.Nullable(payload.GetProperty("ruleError"), RuleError) &&
                        payload.GetProperty("events").ValueKind == JsonValueKind.Array && JsonFields.Object(payload.GetProperty("observation"));
                default: return false;
            }
        }

        private static bool CommandResult(JsonElement value) =>
            JsonFields.Exact(value, "match", "requestSequence", "consumed", "code", "inputSequence", "tick", "accepted", "revision", "ruleError") &&
            JsonFields.Match(value.GetProperty("match")) && JsonFields.Counter(value.GetProperty("requestSequence")) &&
            JsonFields.Boolean(value.GetProperty("consumed")) && JsonFields.Text(value.GetProperty("code")) &&
            JsonFields.Nullable(value.GetProperty("inputSequence"), JsonFields.Counter) && JsonFields.Nullable(value.GetProperty("tick"), JsonFields.Counter) &&
            JsonFields.Boolean(value.GetProperty("accepted")) && JsonFields.Counter(value.GetProperty("revision")) &&
            JsonFields.Nullable(value.GetProperty("ruleError"), RuleError);

        private static bool RoomIdentity(JsonElement value) => JsonFields.Match(value.GetProperty("match")) &&
            JsonFields.Counter(value.GetProperty("revision")) && JsonFields.OneOf(value.GetProperty("status"), "WaitingForPlayers", "WaitingForReconnect", "Running", "Finished", "Closed", "Faulted") &&
            JsonFields.Nullable(value.GetProperty("endReason"), EndReason);

        private static bool EndReason(JsonElement value) => JsonFields.OneOf(value, "GameWon", "PlayerLeft", "ReconnectTimeout", "WaitingTimeout", "IdleTimeout", "ServerStopping", "RoomLost", "ControlUnavailable", "SimulationFaulted", "ResultEncodingFailed", "RoomOverloaded");
        private static bool Token(JsonElement value) => JsonFields.Text(value, 512);
        private static bool RuleError(JsonElement value) => JsonFields.Text(value, 128);

        private static bool Players(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 2) { return false; }
            int lastSeat = -1;
            string? firstPlayer = null;
            foreach (JsonElement player in value.EnumerateArray())
            {
                if (!JsonFields.Exact(player, "playerId", "seat", "online") || !JsonFields.Uuid(player.GetProperty("playerId")) ||
                    !JsonFields.Seat(player.GetProperty("seat")) || !JsonFields.Boolean(player.GetProperty("online"))) { return false; }
                int seat = player.GetProperty("seat").GetInt32();
                string id = player.GetProperty("playerId").GetString()!;
                if (seat <= lastSeat || string.Equals(firstPlayer, id, StringComparison.OrdinalIgnoreCase)) { return false; }
                lastSeat = seat;
                firstPlayer = id;
            }
            return true;
        }
    }
}
