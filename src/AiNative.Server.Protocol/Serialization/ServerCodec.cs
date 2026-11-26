using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AiNative.Server.Protocol.Messages;

namespace AiNative.Server.Protocol.Serialization
{
    /// <summary>内部 v1 信封校验。认证身份必须由 TLS/部署映射提供；业务操作幂等性由目标 Actor 拥有。</summary>
    public static class ServerCodec
    {
        public const int Version = 1;
        public const int MaximumBytes = 256 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool TryDecode(ReadOnlyMemory<byte> bytes, ServerReceiveContext context,
            out ServerMessage? message, out ServerMessageError error)
        {
            ArgumentNullException.ThrowIfNull(context);
            message = null;
            error = ServerMessageError.InvalidMessage;
            if (bytes.Length == 0 || bytes.Length > MaximumBytes) { return false; }
            try
            {
                StrictUtf8.GetCharCount(bytes.Span);
                using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                JsonElement root = document.RootElement;
                if (!JsonFields.Unique(root) || !JsonFields.Exact(root, "serverProtocolVersion", "type", "operationId", "sourceServiceId", "sourceBootId", "targetServiceId", "targetBootId", "controlEpoch", "payload")) { return false; }
                JsonElement version = root.GetProperty("serverProtocolVersion");
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number)) { return false; }
                if (number != Version)
                {
                    error = ServerMessageError.UnsupportedProtocolVersion;
                    return false;
                }
                if (!JsonFields.Text(root.GetProperty("type")) || !JsonFields.Uuid(root.GetProperty("operationId")) ||
                    !JsonFields.Text(root.GetProperty("sourceServiceId")) || !JsonFields.Text(root.GetProperty("targetServiceId")) ||
                    !JsonFields.Uuid(root.GetProperty("sourceBootId")) || !JsonFields.Uuid(root.GetProperty("targetBootId")) ||
                    !JsonFields.Nullable(root.GetProperty("controlEpoch"), JsonFields.Uuid)) { return false; }
                string type = root.GetProperty("type").GetString()!;
                if (root.GetProperty("sourceServiceId").GetString() != context.Remote.ServiceId ||
                    root.GetProperty("sourceBootId").GetGuid() != context.Remote.BootId ||
                    root.GetProperty("targetServiceId").GetString() != context.Local.ServiceId ||
                    root.GetProperty("targetBootId").GetGuid() != context.Local.BootId)
                {
                    error = ServerMessageError.StaleRoute;
                    return false;
                }
                if (!ServerPayloads.IsAllowed(type, context.Remote.Role, context.Local.Role))
                {
                    error = ServerMessageError.Unauthorized;
                    return false;
                }
                JsonElement epochValue = root.GetProperty("controlEpoch");
                Guid? epoch = epochValue.ValueKind == JsonValueKind.Null ? null : epochValue.GetGuid();
                bool registering = type == "RegisterWorker" && epoch == null && context.AllowRegistration;
                if (!registering && (context.ControlEpoch == null || epoch != context.ControlEpoch))
                {
                    error = ServerMessageError.StaleRoute;
                    return false;
                }
                JsonElement payload = root.GetProperty("payload");
                if (!ServerPayloads.Validate(type, payload)) { return false; }
                if (!ServerRouteValidation.MatchesEnvelope(payload, context))
                {
                    error = ServerMessageError.StaleRoute;
                    return false;
                }
                message = new ServerMessage(type, root.GetProperty("operationId").GetGuid(), context.Remote, context.Local, epoch, payload);
                error = ServerMessageError.None;
                return true;
            }
            catch (JsonException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        /// <summary>对接收方视角执行同一校验后编码；不提供未经校验的发送旁路。</summary>
        public static byte[] Encode(string type, Guid operationId, ServerPeer source, ServerPeer target,
            Guid? epoch, JsonElement payload, Guid? receiverEpoch, bool allowRegistration = false)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("serverProtocolVersion", Version);
                writer.WriteString("type", type);
                writer.WriteString("operationId", operationId);
                writer.WriteString("sourceServiceId", source.ServiceId);
                writer.WriteString("sourceBootId", source.BootId);
                writer.WriteString("targetServiceId", target.ServiceId);
                writer.WriteString("targetBootId", target.BootId);
                if (epoch.HasValue) { writer.WriteString("controlEpoch", epoch.Value); }
                else { writer.WriteNull("controlEpoch"); }
                writer.WritePropertyName("payload");
                payload.WriteTo(writer);
                writer.WriteEndObject();
            }
            byte[] bytes = stream.ToArray();
            if (!TryDecode(bytes, new ServerReceiveContext(target, source, receiverEpoch, allowRegistration), out _, out ServerMessageError error))
            {
                throw new ArgumentException($"内部消息校验失败：{error}。");
            }
            return bytes;
        }
    }
}
