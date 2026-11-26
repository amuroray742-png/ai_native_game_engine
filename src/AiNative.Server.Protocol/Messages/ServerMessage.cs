using System;
using System.Text.Json;

namespace AiNative.Server.Protocol.Messages
{
    public enum ServerRole { Gateway, Auth, Lobby, Game }
    public enum ServerMessageError { None, InvalidMessage, UnsupportedProtocolVersion, Unauthorized, StaleRoute }

    /// <summary>由认证链路和本地注册状态提供，不能从待校验正文反推。</summary>
    public sealed record ServerPeer(string ServiceId, Guid BootId, ServerRole Role);

    public sealed record ServerReceiveContext(ServerPeer Local, ServerPeer Remote, Guid? ControlEpoch, bool AllowRegistration = false);

    /// <summary>内部信封独立于客户端版本；Payload 为拥有独立生命周期的副本。</summary>
    public sealed class ServerMessage
    {
        internal ServerMessage(string type, Guid operationId, ServerPeer source, ServerPeer target, Guid? epoch, JsonElement payload)
        {
            Type = type;
            OperationId = operationId;
            Source = source;
            Target = target;
            ControlEpoch = epoch;
            Payload = payload.Clone();
        }

        public string Type { get; }
        public Guid OperationId { get; }
        public ServerPeer Source { get; }
        public ServerPeer Target { get; }
        public Guid? ControlEpoch { get; }
        public JsonElement Payload { get; }
    }
}
