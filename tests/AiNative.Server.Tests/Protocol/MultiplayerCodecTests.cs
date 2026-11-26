using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AiNative.Multiplayer.Protocol.Messages;
using AiNative.Multiplayer.Protocol.Serialization;
using Xunit;

namespace AiNative.Server.Tests.Protocol
{
    public sealed class MultiplayerCodecTests
    {
        private const string Match = "{\"controlEpoch\":\"f7490590-0ae3-4d2b-b9bc-1589c63e1270\",\"roomId\":\"3ecf360e-0afc-4988-90dd-93ba59255820\"}";

        public static IEnumerable<object[]> ClientMessages()
        {
            yield return new object[] { "Hello", "{\"resumeToken\":null}" };
            yield return new object[] { "CreateRoom", "{\"gameId\":\"grid\",\"gameVersion\":\"1\"}" };
            foreach (string type in new[] { "JoinRoom", "ResumeRoom", "GetRoom", "LeaveRoom" })
            {
                yield return new object[] { type, "{\"match\":" + Match + "}" };
            }
            yield return new object[] { "SubmitCommand", "{\"match\":" + Match + ",\"requestSequence\":\"1\",\"expectedTick\":\"0\",\"command\":{\"type\":\"Move\"}}" };
            yield return new object[] { "Ping", "{\"nonce\":\"9223372036854775807\"}" };
            yield return new object[] { "Pong", "{\"nonce\":\"0\"}" };
        }

        [Theory]
        [MemberData(nameof(ClientMessages))]
        public void ClientMessagesRoundTripWithoutBorrowingSourceDocument(string type, string payload)
        {
            MultiplayerMessage message;
            byte[] bytes;
            using (JsonDocument document = JsonDocument.Parse(payload))
            {
                bytes = MultiplayerCodec.Encode(type, "request-1", document.RootElement, MessageDirection.ClientToServer);
                Assert.True(MultiplayerCodec.TryDecode(bytes, MessageDirection.ClientToServer, out MultiplayerMessage? decoded, out MessageError error));
                Assert.Equal(MessageError.None, error);
                message = Assert.IsType<MultiplayerMessage>(decoded);
            }
            Array.Fill(bytes, (byte)0);
            Assert.Equal(type, message.Type);
            Assert.Equal("request-1", message.RequestId);
            Assert.Equal(payload, message.Payload.GetRawText());
        }

        [Theory]
        [InlineData("{\"resumeToken\":null,\"extra\":1}")]
        [InlineData("{}")]
        [InlineData("{\"resumeToken\":3}")]
        [InlineData("{\"resumeToken\":null,\"resumeToken\":null}")]
        [InlineData("[]")]
        public void InvalidHelloPayloadIsRejected(string payload)
        {
            AssertInvalid(Envelope("Hello", payload));
        }

        [Theory]
        [InlineData("01")]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData(" 1")]
        [InlineData("9223372036854775808")]
        [InlineData("１")]
        [InlineData("")]
        public void NoncanonicalCounterIsRejected(string value)
        {
            AssertInvalid(Envelope("Ping", "{\"nonce\":\"" + value + "\"}"));
        }

        [Fact]
        public void VersionDirectionIdsAndUnknownEnvelopeFieldsAreRejected()
        {
            string valid = Envelope("Hello", "{\"resumeToken\":null}");
            byte[] unsupported = Encoding.UTF8.GetBytes(valid.Replace("\"protocolVersion\":1", "\"protocolVersion\":2"));
            Assert.False(MultiplayerCodec.TryDecode(unsupported, MessageDirection.ClientToServer, out _, out MessageError error));
            Assert.Equal(MessageError.UnsupportedProtocolVersion, error);
            AssertInvalid(valid.Replace("\"protocolVersion\":1", "\"protocolVersion\":\"1\""));
            AssertInvalid(valid.Replace("\"request-1\"", "null"));
            AssertInvalid(valid.Replace("request-1", "bad_id"));
            AssertInvalid(valid.Replace("request-1", new string('x', 65)));
            AssertInvalid(valid.Replace("\"type\":", "\"extra\":null,\"type\":"));
            AssertInvalid(valid.Replace("Hello", "Unknown"));
            Assert.False(MultiplayerCodec.TryDecode(Encoding.UTF8.GetBytes(valid), MessageDirection.ServerToClient, out _, out _));
        }

        [Fact]
        public void InvalidUtf8DepthAndDuplicateGameFieldsAreRejected()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Envelope("Hello", "{\"resumeToken\":\"x\"}"));
            bytes[Array.IndexOf(bytes, (byte)'x')] = 0xff;
            Assert.False(MultiplayerCodec.TryDecode(bytes, MessageDirection.ClientToServer, out _, out _));
            Assert.False(MultiplayerCodec.TryDecode(new byte[MultiplayerCodec.MaximumBytes + 1], MessageDirection.ClientToServer, out _, out _));
            string prefix = "{\"match\":" + Match + ",\"requestSequence\":\"1\",\"expectedTick\":\"0\",\"command\":";
            AssertInvalid(Envelope("SubmitCommand", prefix + "{\"x\":1,\"x\":2}}"));
            AssertInvalid(Envelope("SubmitCommand", prefix + new string('[', 20) + "0" + new string(']', 20) + "}"));
        }

        [Fact]
        public void GameIntentValidationIsDeferredToRoomAdapter()
        {
            string payload = "{\"match\":" + Match + ",\"requestSequence\":\"1\",\"expectedTick\":\"0\",\"command\":{\"side\":\"B\"}}";
            Assert.True(MultiplayerCodec.TryDecode(Encoding.UTF8.GetBytes(Envelope("SubmitCommand", payload)), MessageDirection.ClientToServer, out _, out _));
        }

        [Fact]
        public void PushRequiresNullRequestIdAndValidRoomState()
        {
            string payload = "{\"match\":" + Match + ",\"revision\":\"0\",\"status\":\"WaitingForReconnect\",\"players\":[],\"endReason\":null}";
            using JsonDocument document = JsonDocument.Parse(payload);
            byte[] bytes = MultiplayerCodec.Encode("RoomStatus", null, document.RootElement, MessageDirection.ServerToClient);
            Assert.True(MultiplayerCodec.TryDecode(bytes, MessageDirection.ServerToClient, out _, out _));
            Assert.Throws<ArgumentException>(() => MultiplayerCodec.Encode("RoomStatus", "request-1", document.RootElement, MessageDirection.ServerToClient));
        }

        public static IEnumerable<object?[]> ServerMessages()
        {
            string identity = """ "match":MATCH,"revision":"1","status":"Running","endReason":null""".Replace("MATCH", Match);
            string result = """{"match":MATCH,"requestSequence":"1","consumed":true,"code":"Ok","inputSequence":"1","tick":"1","accepted":true,"revision":"1","ruleError":null}""".Replace("MATCH", Match);
            string snapshot = "{" + identity + """, "seat":0,"players":[],"configuration":{},"tick":"0","observation":{},"lastRequestSequence":"0","lastCommandResult":null}""";
            yield return new object?[] { "Welcome", "request-1", """{"controlEpoch":"f7490590-0ae3-4d2b-b9bc-1589c63e1270","playerId":"3ecf360e-0afc-4988-90dd-93ba59255820","resumeToken":"token","connectionGeneration":"1","activeMatch":null}""" };
            yield return new object?[] { "RoomSnapshot", "request-1", snapshot };
            yield return new object?[] { "RoomSnapshot", null, snapshot };
            yield return new object?[] { "RoomLeft", "request-1", """{"match":MATCH}""".Replace("MATCH", Match) };
            yield return new object?[] { "CommandResult", "request-1", result };
            yield return new object?[] { "StateUpdate", null, "{" + identity + """, "tick":"1","inputSequence":"1","actorSeat":0,"requestSequence":"1","accepted":true,"ruleError":null,"events":[],"observation":{}}""" };
            yield return new object?[] { "RoomStatus", null, "{" + identity + """, "players":[]}""" };
            yield return new object?[] { "ServiceStatus", null, """{"code":"ControlUnavailable","match":null}""" };
            yield return new object?[] { "Error", null, """{"code":"InvalidMessage","match":null,"message":"invalid"}""" };
            yield return new object?[] { "Ping", null, """{"nonce":"0"}""" };
            yield return new object?[] { "Pong", "request-1", """{"nonce":"0"}""" };
        }

        [Theory]
        [MemberData(nameof(ServerMessages))]
        public void ServerMessagesRoundTripAndRejectUnknownPayloadFields(string type, string? requestId, string payload)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            byte[] bytes = MultiplayerCodec.Encode(type, requestId, document.RootElement, MessageDirection.ServerToClient);
            Assert.True(MultiplayerCodec.TryDecode(bytes, MessageDirection.ServerToClient, out MultiplayerMessage? message, out _));
            Assert.Equal(type, message!.Type);
            string unknown = payload.Insert(1, """ "unknown":0,""");
            using JsonDocument invalid = JsonDocument.Parse(unknown);
            Assert.Throws<ArgumentException>(() => MultiplayerCodec.Encode(type, requestId, invalid.RootElement, MessageDirection.ServerToClient));
        }

        [Theory]
        [InlineData("""{"resumeToken":"\uD800"}""")]
        [InlineData("""{"\uD800":null}""")]
        public void InvalidUnicodeEscapesReturnInvalidMessage(string payload)
        {
            AssertInvalid(Envelope("Hello", payload));
        }

        [Fact]
        public void InvalidUnicodeInOpaqueCommandIsRejectedAtNetworkBoundary()
        {
            string payload = """{"match":MATCH,"requestSequence":"1","expectedTick":"0","command":{"value":"\uD800"}}""".Replace("MATCH", Match);
            AssertInvalid(Envelope("SubmitCommand", payload));
        }

        private static string Envelope(string type, string payload) =>
            "{\"protocolVersion\":1,\"type\":\"" + type + "\",\"requestId\":\"request-1\",\"payload\":" + payload + "}";

        private static void AssertInvalid(string json)
        {
            Assert.False(MultiplayerCodec.TryDecode(Encoding.UTF8.GetBytes(json), MessageDirection.ClientToServer, out MultiplayerMessage? message, out MessageError error));
            Assert.Null(message);
            Assert.Equal(MessageError.InvalidMessage, error);
        }
    }
}
