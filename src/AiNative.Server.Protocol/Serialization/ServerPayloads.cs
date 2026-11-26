using System.Text;
using System.Text.Json;
using AiNative.Multiplayer.Protocol.Messages;
using AiNative.Multiplayer.Protocol.Serialization;
using AiNative.Server.Protocol.Messages;

namespace AiNative.Server.Protocol.Serialization
{
    internal static class ServerPayloads
    {
        internal static bool IsAllowed(string type, ServerRole source, ServerRole target)
        {
            return type switch
            {
                "LinkPing" or "LinkPong" => source != target && System.Enum.IsDefined(source) && System.Enum.IsDefined(target),
                "RegisterWorker" or "CapacityReport" or "ControlHeartbeat" or "RoomClosed" => source == ServerRole.Game && target == ServerRole.Lobby,
                "AllocateRoom" or "QueryAllocation" or "AdmitPlayer" or "QueryMembership" => source == ServerRole.Lobby && target == ServerRole.Game,
                "RegisterWorkerResult" or "ControlHeartbeatResult" => source == ServerRole.Lobby && target == ServerRole.Game,
                "AuthenticateResult" or "PrepareBindingResult" => source == ServerRole.Auth && target == ServerRole.Gateway,
                "ResolveMembershipResult" => source == ServerRole.Lobby && (target == ServerRole.Auth || target == ServerRole.Gateway),
                "ReserveMembershipResult" => source == ServerRole.Lobby && target == ServerRole.Gateway,
                "WorkerClosed" => source == ServerRole.Lobby && (target == ServerRole.Auth || target == ServerRole.Gateway),
                "AllocationResult" or "MembershipResult" => source == ServerRole.Game && target == ServerRole.Lobby,
                "Authenticate" or "PrepareBinding" => source == ServerRole.Gateway && target == ServerRole.Auth,
                "ResolveMembership" => (source == ServerRole.Auth || source == ServerRole.Gateway) && target == ServerRole.Lobby,
                "ReserveMembership" => source == ServerRole.Gateway && target == ServerRole.Lobby,
                "BindPlayer" => source == ServerRole.Auth && target == ServerRole.Game,
                "BindingResult" => source == ServerRole.Game && target == ServerRole.Auth,
                "RelayCommand" or "RoomRequest" or "GatewayRouteLost" => source == ServerRole.Gateway && target == ServerRole.Game,
                "RoomEnvelope" => source == ServerRole.Game && target == ServerRole.Gateway,
                _ => false
            };
        }

        internal static bool Validate(string type, JsonElement payload)
        {
            switch (type)
            {
                case "RegisterWorker":
                    return JsonFields.Exact(payload, "gameId", "gameVersion", "configurationDigest", "maxRoomThreads") &&
                        JsonFields.Text(payload.GetProperty("gameId")) && JsonFields.Text(payload.GetProperty("gameVersion")) &&
                        Digest(payload.GetProperty("configurationDigest")) && PositiveCounter(payload.GetProperty("maxRoomThreads"));
                case "CapacityReport":
                    return JsonFields.Exact(payload, "activeThreads", "reservedThreads", "draining") &&
                        JsonFields.Counter(payload.GetProperty("activeThreads")) && JsonFields.Counter(payload.GetProperty("reservedThreads")) && JsonFields.Boolean(payload.GetProperty("draining"));
                case "RegisterWorkerResult":
                    return JsonFields.Exact(payload, "code") && Code(payload.GetProperty("code"));
                case "LinkPing": case "LinkPong":
                case "ControlHeartbeatResult":
                case "ControlHeartbeat":
                    return JsonFields.Exact(payload, "nonce") && JsonFields.Counter(payload.GetProperty("nonce"));
                case "AllocateRoom":
                    return JsonFields.Exact(payload, "allocationId", "roomId", "gameId", "gameVersion", "configurationDigest") &&
                        JsonFields.Uuid(payload.GetProperty("allocationId")) && JsonFields.Uuid(payload.GetProperty("roomId")) &&
                        JsonFields.Text(payload.GetProperty("gameId")) && JsonFields.Text(payload.GetProperty("gameVersion")) && Digest(payload.GetProperty("configurationDigest"));
                case "QueryAllocation":
                    return JsonFields.Exact(payload, "allocationId") && JsonFields.Uuid(payload.GetProperty("allocationId"));
                case "AllocationResult":
                    return JsonFields.Exact(payload, "allocationId", "code", "route") && JsonFields.Uuid(payload.GetProperty("allocationId")) &&
                        Code(payload.GetProperty("code")) && ResultRoute(payload);
                case "ResolveMembership":
                    return JsonFields.Exact(payload, "playerId") && JsonFields.Uuid(payload.GetProperty("playerId"));
                case "ReserveMembership":
                    return JsonFields.Exact(payload, "membershipOperationId", "playerId", "intent") &&
                        MemberIdentity(payload) && MembershipIntent(payload.GetProperty("intent"));
                case "AdmitPlayer": case "QueryMembership":
                    return JsonFields.Exact(payload, "membershipOperationId", "playerId", "route") && MemberIdentity(payload) && RoomRoute(payload.GetProperty("route"));
                case "MembershipResult":
                    return JsonFields.Exact(payload, "membershipOperationId", "playerId", "route", "code", "seat") && MemberIdentity(payload) &&
                        RoomRoute(payload.GetProperty("route")) && Code(payload.GetProperty("code")) &&
                        (IsOk(payload) ? JsonFields.Seat(payload.GetProperty("seat")) : payload.GetProperty("seat").ValueKind == JsonValueKind.Null);
                case "Authenticate":
                    return JsonFields.Exact(payload, "connection", "resumeToken") && ConnectionRoute(payload.GetProperty("connection")) &&
                        JsonFields.Nullable(payload.GetProperty("resumeToken"), Token);
                case "AuthenticateResult":
                    return JsonFields.Exact(payload, "code", "connection", "playerId", "resumeToken", "activeMatch") &&
                        Code(payload.GetProperty("code")) && ConnectionRoute(payload.GetProperty("connection")) &&
                        JsonFields.Nullable(payload.GetProperty("playerId"), JsonFields.Uuid) && JsonFields.Nullable(payload.GetProperty("resumeToken"), Token) &&
                        JsonFields.Nullable(payload.GetProperty("activeMatch"), JsonFields.Match) && AuthenticationResult(payload);
                case "ResolveMembershipResult":
                    return JsonFields.Exact(payload, "code", "playerId", "membershipOperationId", "route") &&
                        Code(payload.GetProperty("code")) && JsonFields.Uuid(payload.GetProperty("playerId")) &&
                        JsonFields.Nullable(payload.GetProperty("membershipOperationId"), JsonFields.Uuid) && JsonFields.Nullable(payload.GetProperty("route"), RoomRoute) &&
                        ((payload.GetProperty("membershipOperationId").ValueKind == JsonValueKind.Null) == (payload.GetProperty("route").ValueKind == JsonValueKind.Null));
                case "ReserveMembershipResult":
                    return JsonFields.Exact(payload, "code", "membershipOperationId", "playerId", "route") &&
                        MemberIdentity(payload) && Code(payload.GetProperty("code")) && ResultRoute(payload);
                case "WorkerClosed":
                    return JsonFields.Exact(payload, "workerId", "workerBootId", "reason") && JsonFields.Text(payload.GetProperty("workerId")) &&
                        JsonFields.Uuid(payload.GetProperty("workerBootId")) && Reason(payload.GetProperty("reason"));
                case "PrepareBinding": case "BindPlayer":
                    return JsonFields.Exact(payload, "playerId", "route", "connection") && JsonFields.Uuid(payload.GetProperty("playerId")) &&
                        RoomRoute(payload.GetProperty("route")) && ConnectionRoute(payload.GetProperty("connection"));
                case "PrepareBindingResult": case "BindingResult":
                    return JsonFields.Exact(payload, "playerId", "route", "connection", "code") && JsonFields.Uuid(payload.GetProperty("playerId")) &&
                        RoomRoute(payload.GetProperty("route")) && ConnectionRoute(payload.GetProperty("connection")) && Code(payload.GetProperty("code"));
                case "RoomRequest":
                    return JsonFields.Exact(payload, "playerId", "route", "connection", "requestId", "requestType") &&
                        JsonFields.Uuid(payload.GetProperty("playerId")) && RoomRoute(payload.GetProperty("route")) && ConnectionRoute(payload.GetProperty("connection")) &&
                        RequestId(payload.GetProperty("requestId")) && JsonFields.OneOf(payload.GetProperty("requestType"), "ResumeRoom", "GetRoom", "LeaveRoom");
                case "RelayCommand":
                    return JsonFields.Exact(payload, "playerId", "route", "connection", "requestId", "requestSequence", "expectedTick", "command") &&
                        JsonFields.Uuid(payload.GetProperty("playerId")) && RoomRoute(payload.GetProperty("route")) && ConnectionRoute(payload.GetProperty("connection")) &&
                        RequestId(payload.GetProperty("requestId")) && JsonFields.Counter(payload.GetProperty("requestSequence")) &&
                        JsonFields.Counter(payload.GetProperty("expectedTick")) && JsonFields.Object(payload.GetProperty("command"));
                case "RoomEnvelope":
                    return JsonFields.Exact(payload, "route", "connection", "message") && RoomRoute(payload.GetProperty("route")) &&
                        ConnectionRoute(payload.GetProperty("connection")) && ClientMessage(payload.GetProperty("message")) && RoomMessageMatchesRoute(payload);
                case "GatewayRouteLost":
                    return JsonFields.Exact(payload, "route", "connection") && RoomRoute(payload.GetProperty("route")) && ConnectionRoute(payload.GetProperty("connection"));
                case "RoomClosed":
                    return JsonFields.Exact(payload, "route", "reason") && RoomRoute(payload.GetProperty("route")) && Reason(payload.GetProperty("reason"));
                default: return false;
            }
        }

        private static bool MembershipIntent(JsonElement value)
        {
            if (JsonFields.Exact(value, "type", "gameId", "gameVersion"))
            {
                return JsonFields.OneOf(value.GetProperty("type"), "Create") && JsonFields.Text(value.GetProperty("gameId")) && JsonFields.Text(value.GetProperty("gameVersion"));
            }
            return JsonFields.Exact(value, "type", "match") && JsonFields.OneOf(value.GetProperty("type"), "Join") && JsonFields.Match(value.GetProperty("match"));
        }

        private static bool ClientMessage(JsonElement value) => MultiplayerCodec.TryDecode(
            Encoding.UTF8.GetBytes(value.GetRawText()), MessageDirection.ServerToClient, out _, out _);

        private static bool RoomMessageMatchesRoute(JsonElement payload)
        {
            JsonElement message = payload.GetProperty("message");
            if (!JsonFields.OneOf(message.GetProperty("type"), "RoomSnapshot", "CommandResult", "StateUpdate", "RoomStatus", "RoomLeft", "Error")) { return false; }
            JsonElement match = message.GetProperty("payload").GetProperty("match");
            JsonElement route = payload.GetProperty("route");
            return match.ValueKind == JsonValueKind.Object &&
                match.GetProperty("controlEpoch").GetGuid() == route.GetProperty("controlEpoch").GetGuid() &&
                match.GetProperty("roomId").GetGuid() == route.GetProperty("roomId").GetGuid();
        }

        private static bool RequestId(JsonElement value)
        {
            if (!JsonFields.Text(value, 64)) { return false; }
            foreach (char character in value.GetString()!)
            {
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') && character != '-') { return false; }
            }
            return true;
        }

        private static bool MemberIdentity(JsonElement payload) => JsonFields.Uuid(payload.GetProperty("membershipOperationId")) && JsonFields.Uuid(payload.GetProperty("playerId"));
        private static bool PositiveCounter(JsonElement value) => JsonFields.Counter(value) && value.GetString() != "0";
        private static bool Code(JsonElement value) => JsonFields.OneOf(value, "Ok", "Busy", "Rejected", "NotAllocated", "NotAdmitted", "Pending", "OperationConflict", "OperationExpired", "RoomLost", "StaleRoute", "StaleConnection", "InvalidResumeToken", "SessionExpired", "AlreadyInRoom", "RoomFull", "NotRoomMember", "RoomNotFound", "ControlUnavailable", "ServiceUnavailable", "OutcomeUnknown");
        private static bool IsOk(JsonElement payload) => payload.GetProperty("code").GetString() == "Ok";
        private static bool ResultRoute(JsonElement payload) => IsOk(payload) ? RoomRoute(payload.GetProperty("route")) : payload.GetProperty("route").ValueKind == JsonValueKind.Null;
        private static bool AuthenticationResult(JsonElement payload)
        {
            if (IsOk(payload))
            {
                return JsonFields.Uuid(payload.GetProperty("playerId")) && Token(payload.GetProperty("resumeToken")) &&
                    PositiveCounter(payload.GetProperty("connection").GetProperty("connectionGeneration"));
            }
            return payload.GetProperty("playerId").ValueKind == JsonValueKind.Null && payload.GetProperty("resumeToken").ValueKind == JsonValueKind.Null &&
                payload.GetProperty("activeMatch").ValueKind == JsonValueKind.Null;
        }
        private static bool Reason(JsonElement value) => JsonFields.OneOf(value, "GameWon", "PlayerLeft", "ReconnectTimeout", "WaitingTimeout", "IdleTimeout", "ServerStopping", "RoomLost", "ControlUnavailable", "SimulationFaulted", "ResultEncodingFailed", "RoomOverloaded");
        private static bool Token(JsonElement value) => JsonFields.Text(value, 512);

        private static bool Digest(JsonElement value)
        {
            if (!JsonFields.Text(value, 64) || value.GetString()!.Length != 64) { return false; }
            foreach (char character in value.GetString()!)
            {
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')) { return false; }
            }
            return true;
        }

        private static bool RoomRoute(JsonElement value) =>
            JsonFields.Exact(value, "controlEpoch", "roomId", "workerId", "workerBootId", "roomIncarnation") &&
            JsonFields.Uuid(value.GetProperty("controlEpoch")) && JsonFields.Uuid(value.GetProperty("roomId")) &&
            JsonFields.Text(value.GetProperty("workerId")) && JsonFields.Uuid(value.GetProperty("workerBootId")) && JsonFields.Uuid(value.GetProperty("roomIncarnation"));

        private static bool ConnectionRoute(JsonElement value) =>
            JsonFields.Exact(value, "gatewayId", "gatewayBootId", "connectionId", "connectionGeneration") &&
            JsonFields.Text(value.GetProperty("gatewayId")) && JsonFields.Uuid(value.GetProperty("gatewayBootId")) &&
            JsonFields.Uuid(value.GetProperty("connectionId")) && JsonFields.Counter(value.GetProperty("connectionGeneration"));
    }
}
