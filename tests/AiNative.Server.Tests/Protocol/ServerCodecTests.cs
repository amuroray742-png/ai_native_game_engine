using System;
using System.Text;
using System.Text.Json;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Protocol.Serialization;
using Xunit;

namespace AiNative.Server.Tests.Protocol
{
    public sealed class ServerCodecTests
    {
        private static readonly ServerPeer Worker = new ServerPeer("worker-1", Guid.NewGuid(), ServerRole.Game);
        private static readonly ServerPeer Lobby = new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby);
        private static readonly Guid Epoch = Guid.NewGuid();

        [Fact]
        public void HeartbeatRoundTripsWithAuthenticatedPeers()
        {
            byte[] bytes = Heartbeat();
            Assert.True(ServerCodec.TryDecode(bytes, Context(), out ServerMessage? message, out ServerMessageError error));
            Assert.Equal(ServerMessageError.None, error);
            Assert.Equal(Worker, message!.Source);
            Assert.Equal(Lobby, message.Target);
            Assert.Equal(Epoch, message.ControlEpoch);
            Array.Fill(bytes, (byte)0);
            Assert.Equal("1", message.Payload.GetProperty("nonce").GetString());
        }

        [Fact]
        public void SourceTargetAndEpochMustMatchAuthenticatedContext()
        {
            byte[] bytes = Heartbeat();
            AssertRejected(bytes, Context() with { Remote = Worker with { ServiceId = "worker-2" } }, ServerMessageError.StaleRoute);
            AssertRejected(bytes, Context() with { Remote = Worker with { BootId = Guid.NewGuid() } }, ServerMessageError.StaleRoute);
            AssertRejected(bytes, Context() with { Local = Lobby with { ServiceId = "lobby-2" } }, ServerMessageError.StaleRoute);
            AssertRejected(bytes, Context() with { Local = Lobby with { BootId = Guid.NewGuid() } }, ServerMessageError.StaleRoute);
            AssertRejected(bytes, Context() with { ControlEpoch = Guid.NewGuid() }, ServerMessageError.StaleRoute);
            AssertRejected(bytes, Context() with { ControlEpoch = null }, ServerMessageError.StaleRoute);
            AssertRejected(bytes, Context() with { Remote = Worker with { Role = ServerRole.Gateway } }, ServerMessageError.Unauthorized);
        }

        [Fact]
        public void NullEpochOnlyAllowedForExplicitWorkerRegistrationPhase()
        {
            using JsonDocument payload = JsonDocument.Parse("""{"gameId":"grid","gameVersion":"1","configurationDigest":"DIGEST","maxRoomThreads":"100"}""".Replace("DIGEST", new string('a', 64)));
            byte[] bytes = ServerCodec.Encode("RegisterWorker", Guid.NewGuid(), Worker, Lobby, null, payload.RootElement, Epoch, true);
            Assert.True(ServerCodec.TryDecode(bytes, Context() with { AllowRegistration = true }, out _, out _));
            AssertRejected(bytes, Context(), ServerMessageError.StaleRoute);
            string wrongType = Encoding.UTF8.GetString(bytes).Replace("RegisterWorker", "CapacityReport");
            AssertRejected(Encoding.UTF8.GetBytes(wrongType), Context() with { AllowRegistration = true }, ServerMessageError.StaleRoute);
        }

        [Theory]
        [InlineData("{\"nonce\":\"01\"}")]
        [InlineData("{\"nonce\":1}")]
        [InlineData("{\"nonce\":\"1\",\"nonce\":\"2\"}")]
        [InlineData("{\"nonce\":\"1\",\"sourceRole\":\"Lobby\"}")]
        [InlineData("{}")]
        public void InvalidPayloadCannotBeEncoded(string json)
        {
            using JsonDocument payload = JsonDocument.Parse(json);
            Assert.Throws<ArgumentException>(() => ServerCodec.Encode("ControlHeartbeat", Guid.NewGuid(), Worker, Lobby, Epoch, payload.RootElement, Epoch));
        }

        [Fact]
        public void VersionUnknownFieldsAndUnicodeAreRejected()
        {
            string json = Encoding.UTF8.GetString(Heartbeat());
            AssertRejected(Encoding.UTF8.GetBytes(json.Replace("\"serverProtocolVersion\":1", "\"serverProtocolVersion\":2")), Context(), ServerMessageError.UnsupportedProtocolVersion);
            AssertRejected(Encoding.UTF8.GetBytes(json.Insert(1, "\"unknown\":0,")), Context(), ServerMessageError.InvalidMessage);
            AssertRejected(Encoding.UTF8.GetBytes(json.Replace("\"nonce\":\"1\"", """ "nonce":"\uD800" """)), Context(), ServerMessageError.InvalidMessage);
        }

        private static byte[] Heartbeat()
        {
            using JsonDocument payload = JsonDocument.Parse("""{"nonce":"1"}""");
            return ServerCodec.Encode("ControlHeartbeat", Guid.NewGuid(), Worker, Lobby, Epoch, payload.RootElement, Epoch);
        }

        private static ServerReceiveContext Context() => new ServerReceiveContext(Lobby, Worker, Epoch);

        private static void AssertRejected(byte[] bytes, ServerReceiveContext context, ServerMessageError expected)
        {
            Assert.False(ServerCodec.TryDecode(bytes, context, out ServerMessage? message, out ServerMessageError error));
            Assert.Null(message);
            Assert.Equal(expected, error);
        }
    }
}
