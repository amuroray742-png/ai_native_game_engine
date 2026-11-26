using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Protocol.Serialization;
using Xunit;

namespace AiNative.Server.Tests.Protocol
{
    public sealed class ServerRoutingTests
    {
        private static readonly Guid Epoch = Guid.NewGuid();
        private static readonly Guid Player = Guid.NewGuid();
        private static readonly Guid Member = Guid.NewGuid();
        private static readonly Guid Allocation = Guid.NewGuid();
        private static readonly ServerPeer Gateway = new ServerPeer("gateway-1", Guid.NewGuid(), ServerRole.Gateway);
        private static readonly ServerPeer Auth = new ServerPeer("auth-1", Guid.NewGuid(), ServerRole.Auth);
        private static readonly ServerPeer Lobby = new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby);
        private static readonly ServerPeer Worker = new ServerPeer("worker-1", Guid.NewGuid(), ServerRole.Game);
        private static readonly RoomRoute Room = new RoomRoute(Epoch, Guid.NewGuid(), Worker.ServiceId, Worker.BootId, Guid.NewGuid());
        private static readonly ConnectionRoute Connection = new ConnectionRoute(Gateway.ServiceId, Gateway.BootId, Guid.NewGuid(), 2);
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static object Route => new { controlEpoch = Epoch, roomId = Room.RoomId, workerId = Worker.ServiceId, workerBootId = Worker.BootId, roomIncarnation = Room.RoomIncarnation };
        private static object Link => new { gatewayId = Gateway.ServiceId, gatewayBootId = Gateway.BootId, connectionId = Connection.ConnectionId, connectionGeneration = "2" };
        private static object Match => new { controlEpoch = Epoch, roomId = Room.RoomId };

        public static IEnumerable<object[]> Messages()
        {
            yield return Row("RegisterWorker", Worker, Lobby, new { gameId = "grid", gameVersion = "1", configurationDigest = new string('a', 64), maxRoomThreads = "100" });
            yield return Row("RegisterWorkerResult", Lobby, Worker, new { code = "Ok" });
            yield return Row("CapacityReport", Worker, Lobby, new { activeThreads = "1", reservedThreads = "0", draining = false });
            yield return Row("ControlHeartbeat", Worker, Lobby, new { nonce = "1" });
            yield return Row("ControlHeartbeatResult", Lobby, Worker, new { nonce = "1" });
            yield return Row("AllocateRoom", Lobby, Worker, new { allocationId = Allocation, roomId = Room.RoomId, gameId = "grid", gameVersion = "1", configurationDigest = new string('a', 64) });
            yield return Row("QueryAllocation", Lobby, Worker, new { allocationId = Allocation });
            yield return Row("AllocationResult", Worker, Lobby, new { allocationId = Allocation, code = "Ok", route = Route });
            yield return Row("ResolveMembership", Auth, Lobby, new { playerId = Player });
            yield return Row("ResolveMembershipResult", Lobby, Auth, new { playerId = Player, code = "Ok", membershipOperationId = Member, route = Route });
            yield return Row("ReserveMembership", Gateway, Lobby, new { membershipOperationId = Member, playerId = Player, intent = new { type = "Join", match = Match } });
            yield return Row("ReserveMembership", Gateway, Lobby, new { membershipOperationId = Member, playerId = Player, intent = new { type = "Create", gameId = "grid", gameVersion = "1" } });
            yield return Row("ReserveMembershipResult", Lobby, Gateway, new { membershipOperationId = Member, playerId = Player, code = "Ok", route = Route });
            yield return Row("AdmitPlayer", Lobby, Worker, new { membershipOperationId = Member, playerId = Player, route = Route });
            yield return Row("QueryMembership", Lobby, Worker, new { membershipOperationId = Member, playerId = Player, route = Route });
            yield return Row("MembershipResult", Worker, Lobby, new { membershipOperationId = Member, playerId = Player, route = Route, code = "Ok", seat = 0 });
            yield return Row("Authenticate", Gateway, Auth, new { connection = Link, resumeToken = (string?)null });
            yield return Row("AuthenticateResult", Auth, Gateway, new { connection = Link, code = "Ok", playerId = Player, resumeToken = "opaque", activeMatch = Match });
            yield return Row("PrepareBinding", Gateway, Auth, new { playerId = Player, route = Route, connection = Link });
            yield return Row("BindPlayer", Auth, Worker, new { playerId = Player, route = Route, connection = Link });
            yield return Row("PrepareBindingResult", Auth, Gateway, new { playerId = Player, route = Route, connection = Link, code = "Ok" });
            yield return Row("BindingResult", Worker, Auth, new { playerId = Player, route = Route, connection = Link, code = "Ok" });
            yield return Row("RoomRequest", Gateway, Worker, new { playerId = Player, route = Route, connection = Link, requestId = "resume-1", requestType = "ResumeRoom" });
            yield return Row("RelayCommand", Gateway, Worker, Command());
            yield return Row("RoomEnvelope", Worker, Gateway, new { route = Route, connection = Link, message = new { protocolVersion = 1, type = "RoomStatus", requestId = (string?)null, payload = new { match = Match, revision = "1", status = "Running", players = Array.Empty<object>(), endReason = (string?)null } } });
            yield return Row("GatewayRouteLost", Gateway, Worker, new { route = Route, connection = Link });
            yield return Row("RoomClosed", Worker, Lobby, new { route = Route, reason = "PlayerLeft" });
            yield return Row("WorkerClosed", Lobby, Gateway, new { workerId = Worker.ServiceId, workerBootId = Worker.BootId, reason = "RoomLost" });
        }

        [Theory]
        [MemberData(nameof(Messages))]
        public void EveryMessageHasStrictPayloadAndAuthorizedRoundTrip(string type, ServerPeer source, ServerPeer target, string payload)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            byte[] bytes = ServerCodec.Encode(type, Guid.NewGuid(), source, target, Epoch, document.RootElement, Epoch);
            var context = new ServerReceiveContext(target, source, Epoch);
            Assert.True(ServerCodec.TryDecode(bytes, context, out _, out _));
            JsonNode root = JsonNode.Parse(bytes)!;
            root["payload"]!["unknown"] = true;
            Assert.False(ServerCodec.TryDecode(JsonSerializer.SerializeToUtf8Bytes(root), context, out _, out ServerMessageError error));
            Assert.Equal(ServerMessageError.InvalidMessage, error);
            root["payload"]!.AsObject().Remove("unknown");
            string first = string.Empty;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            { first = property.Name; break; }
            root["payload"]!.AsObject().Remove(first);
            Assert.False(ServerCodec.TryDecode(JsonSerializer.SerializeToUtf8Bytes(root), context, out _, out error));
            Assert.Equal(ServerMessageError.InvalidMessage, error);
        }

        [Theory]
        [InlineData("route", "controlEpoch")]
        [InlineData("route", "workerBootId")]
        [InlineData("connection", "gatewayBootId")]
        public void NestedBootAndEpochMustMatchEnvelope(string part, string field)
        {
            byte[] bytes = CommandBytes();
            JsonNode root = JsonNode.Parse(bytes)!;
            root["payload"]![part]![field] = Guid.NewGuid().ToString("D");
            Assert.False(ServerCodec.TryDecode(JsonSerializer.SerializeToUtf8Bytes(root), new ServerReceiveContext(Worker, Gateway, Epoch), out _, out ServerMessageError error));
            Assert.Equal(ServerMessageError.StaleRoute, error);
        }

        [Fact]
        public void RoomActorChecksIncarnationPlayerAndConnectionGeneration()
        {
            Assert.True(ServerCodec.TryDecode(CommandBytes(), new ServerReceiveContext(Worker, Gateway, Epoch), out ServerMessage? message, out _));
            var binding = new PlayerBinding(Player, Connection);
            Assert.Equal(ServerMessageError.None, ServerRouteValidation.CheckPlayer(message!, Room, binding));
            Assert.Equal(ServerMessageError.StaleRoute, ServerRouteValidation.CheckPlayer(message!, Room with { RoomIncarnation = Guid.NewGuid() }, binding));
            Assert.Equal(ServerMessageError.StaleRoute, ServerRouteValidation.CheckPlayer(message!, Room, binding with { Connection = Connection with { ConnectionGeneration = 3 } }));
            Assert.Equal(ServerMessageError.Unauthorized, ServerRouteValidation.CheckPlayer(message!, Room, binding with { PlayerId = Guid.NewGuid() }));
        }

        [Theory]
        [InlineData(ServerRole.Auth)]
        [InlineData(ServerRole.Lobby)]
        [InlineData(ServerRole.Game)]
        public void OnlyGatewayCanRelayCommands(ServerRole role)
        {
            Assert.False(ServerCodec.TryDecode(CommandBytes(), new ServerReceiveContext(Worker, Gateway with { Role = role }, Epoch), out _, out ServerMessageError error));
            Assert.Equal(ServerMessageError.Unauthorized, error);
        }

        [Theory]
        [InlineData("roomId")]
        [InlineData("controlEpoch")]
        public void RoomEnvelopeRejectsClientMessageForAnotherMatch(string field)
        {
            JsonElement sample = default;
            foreach (object[] row in Messages())
            {
                if ((string)row[0] == "RoomEnvelope")
                { sample = JsonSerializer.Deserialize<JsonElement>((string)row[3]); break; }
            }
            JsonNode payload = JsonNode.Parse(sample.GetRawText())!;
            payload["message"]!["payload"]!["match"]![field] = Guid.NewGuid().ToString("D");
            Assert.Throws<ArgumentException>(() => ServerCodec.Encode("RoomEnvelope", Guid.NewGuid(), Worker, Gateway, Epoch,
                JsonSerializer.SerializeToElement(payload), Epoch));
        }

        private static object Command() => new { playerId = Player, route = Route, connection = Link, requestId = "command-1", requestSequence = "1", expectedTick = "0", command = new { type = "EndTurn" } };
        private static object[] Row(string type, ServerPeer source, ServerPeer target, object payload) => new object[] { type, source, target, JsonSerializer.Serialize(payload, Options) };
        private static byte[] CommandBytes() => ServerCodec.Encode("RelayCommand", Guid.NewGuid(), Gateway, Worker, Epoch, JsonSerializer.SerializeToElement(Command(), Options), Epoch);
    }
}
