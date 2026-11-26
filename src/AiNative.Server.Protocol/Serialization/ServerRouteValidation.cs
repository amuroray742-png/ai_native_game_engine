using System.Text.Json;
using AiNative.Server.Protocol.Messages;

namespace AiNative.Server.Protocol.Serialization
{
    /// <summary>信封校验之后，目标 Actor 仍须以当前权威状态验证房间和玩家代次。</summary>
    public static class ServerRouteValidation
    {
        public static ServerMessageError CheckRoom(ServerMessage message, RoomRoute currentRoom)
        {
            if (!message.Payload.TryGetProperty("route", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return ServerMessageError.InvalidMessage;
            }
            return RoomRoute.Read(value) == currentRoom ? ServerMessageError.None : ServerMessageError.StaleRoute;
        }

        public static ServerMessageError CheckPlayer(ServerMessage message, RoomRoute currentRoom, PlayerBinding currentBinding)
        {
            ServerMessageError roomError = CheckRoom(message, currentRoom);
            if (roomError != ServerMessageError.None) { return roomError; }
            if (!message.Payload.TryGetProperty("playerId", out JsonElement player) ||
                !message.Payload.TryGetProperty("connection", out JsonElement connection))
            {
                return ServerMessageError.InvalidMessage;
            }
            if (player.GetGuid() != currentBinding.PlayerId) { return ServerMessageError.Unauthorized; }
            return ConnectionRoute.Read(connection) == currentBinding.Connection ? ServerMessageError.None : ServerMessageError.StaleRoute;
        }

        internal static bool MatchesEnvelope(JsonElement payload, ServerReceiveContext context)
        {
            if (payload.TryGetProperty("route", out JsonElement route) && route.ValueKind != JsonValueKind.Null)
            {
                RoomRoute room = RoomRoute.Read(route);
                if (room.ControlEpoch != context.ControlEpoch) { return false; }
                ServerPeer? worker = context.Local.Role == ServerRole.Game ? context.Local :
                    context.Remote.Role == ServerRole.Game ? context.Remote : null;
                if (worker != null && (room.WorkerId != worker.ServiceId || room.WorkerBootId != worker.BootId)) { return false; }
            }
            if (payload.TryGetProperty("connection", out JsonElement connection) && connection.ValueKind != JsonValueKind.Null)
            {
                ConnectionRoute target = ConnectionRoute.Read(connection);
                ServerPeer? gateway = context.Local.Role == ServerRole.Gateway ? context.Local :
                    context.Remote.Role == ServerRole.Gateway ? context.Remote : null;
                if (gateway != null && (target.GatewayId != gateway.ServiceId || target.GatewayBootId != gateway.BootId)) { return false; }
            }
            if (payload.TryGetProperty("match", out JsonElement match) && match.ValueKind != JsonValueKind.Null &&
                match.GetProperty("controlEpoch").GetGuid() != context.ControlEpoch) { return false; }
            if (payload.TryGetProperty("activeMatch", out JsonElement activeMatch) && activeMatch.ValueKind != JsonValueKind.Null &&
                activeMatch.GetProperty("controlEpoch").GetGuid() != context.ControlEpoch) { return false; }
            if (payload.TryGetProperty("intent", out JsonElement intent) && intent.TryGetProperty("match", out JsonElement intendedMatch) &&
                intendedMatch.GetProperty("controlEpoch").GetGuid() != context.ControlEpoch) { return false; }
            return true;
        }
    }
}
