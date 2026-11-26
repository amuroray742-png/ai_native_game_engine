using System;
using System.Globalization;
using System.Text.Json;

namespace AiNative.Server.Protocol.Messages
{
    public sealed record RoomRoute(Guid ControlEpoch, Guid RoomId, string WorkerId, Guid WorkerBootId, Guid RoomIncarnation)
    {
        internal static RoomRoute Read(JsonElement value) => new RoomRoute(
            value.GetProperty("controlEpoch").GetGuid(), value.GetProperty("roomId").GetGuid(),
            value.GetProperty("workerId").GetString()!, value.GetProperty("workerBootId").GetGuid(),
            value.GetProperty("roomIncarnation").GetGuid());
    }

    public sealed record ConnectionRoute(string GatewayId, Guid GatewayBootId, Guid ConnectionId, long ConnectionGeneration)
    {
        internal static ConnectionRoute Read(JsonElement value) => new ConnectionRoute(
            value.GetProperty("gatewayId").GetString()!, value.GetProperty("gatewayBootId").GetGuid(),
            value.GetProperty("connectionId").GetGuid(),
            long.Parse(value.GetProperty("connectionGeneration").GetString()!, CultureInfo.InvariantCulture));
    }

    /// <summary>由房间维护的当前绑定。网络消息不能创建或覆盖它。</summary>
    public sealed record PlayerBinding(Guid PlayerId, ConnectionRoute Connection);
}
