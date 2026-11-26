using System.Text.Json;

namespace AiNative.Multiplayer.Protocol.Messages
{
    public enum MessageDirection { ClientToServer, ServerToClient }
    public enum MessageError { None, InvalidMessage, UnsupportedProtocolVersion }

    /// <summary>经过校验的独立消息副本。Payload 的游戏字段仍须由游戏适配校验。</summary>
    public sealed class MultiplayerMessage
    {
        internal MultiplayerMessage(string type, string? requestId, JsonElement payload)
        {
            Type = type;
            RequestId = requestId;
            Payload = payload.Clone();
        }

        public string Type { get; }
        public string? RequestId { get; }
        public JsonElement Payload { get; }
    }
}
