using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AiNative.Server.Protocol.Messages;

namespace AiNative.Server.Protocol.Serialization
{
    public sealed record ServerHello(ServerPeer Peer, Guid? ControlEpoch);

    /// <summary>TLS 完成后的首帧，绑定证书身份与本次 bootId。不能通过它确认房间或 Worker 已注册。</summary>
    public static class ServerHelloCodec
    {
        public const int MaximumBytes = 4096;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(ServerHello hello)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("serverProtocolVersion", ServerCodec.Version);
                writer.WriteString("type", "LinkHello");
                writer.WriteString("serviceId", hello.Peer.ServiceId);
                writer.WriteString("bootId", hello.Peer.BootId);
                writer.WriteString("role", hello.Peer.Role.ToString());
                if (hello.ControlEpoch.HasValue) { writer.WriteString("controlEpoch", hello.ControlEpoch.Value); }
                else { writer.WriteNull("controlEpoch"); }
                writer.WriteEndObject();
            }
            byte[] bytes = stream.ToArray();
            if (!TryDecode(bytes, hello.Peer.ServiceId, hello.Peer.Role, out _)) { throw new ArgumentException("无效的链路握手身份。"); }
            return bytes;
        }

        public static bool TryDecode(ReadOnlyMemory<byte> bytes, string authenticatedServiceId, ServerRole authenticatedRole, out ServerHello? hello)
        {
            hello = null;
            if (!Enum.IsDefined(authenticatedRole) || bytes.Length == 0 || bytes.Length > MaximumBytes) { return false; }
            try
            {
                StrictUtf8.GetCharCount(bytes.Span);
                using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                JsonElement root = document.RootElement;
                if (!JsonFields.Unique(root) || !JsonFields.Exact(root, "serverProtocolVersion", "type", "serviceId", "bootId", "role", "controlEpoch")) { return false; }
                JsonElement version = root.GetProperty("serverProtocolVersion");
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != ServerCodec.Version ||
                    !JsonFields.OneOf(root.GetProperty("type"), "LinkHello") || !JsonFields.Text(root.GetProperty("serviceId")) ||
                    root.GetProperty("serviceId").GetString() != authenticatedServiceId || !JsonFields.OneOf(root.GetProperty("role"), authenticatedRole.ToString()) ||
                    !JsonFields.Uuid(root.GetProperty("bootId")) || root.GetProperty("bootId").GetGuid() == Guid.Empty ||
                    !JsonFields.Nullable(root.GetProperty("controlEpoch"), JsonFields.Uuid)) { return false; }
                JsonElement epoch = root.GetProperty("controlEpoch");
                hello = new ServerHello(new ServerPeer(authenticatedServiceId, root.GetProperty("bootId").GetGuid(), authenticatedRole),
                    epoch.ValueKind == JsonValueKind.Null ? null : epoch.GetGuid());
                return true;
            }
            catch (JsonException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }
    }
}
