using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiNative.Server.Configuration;
using AiNative.Server.Protocol.Messages;
using Xunit;

namespace AiNative.Server.Tests.Configuration
{
    public sealed class DeploymentConfigurationTests
    {
        [Fact]
        public void SixInstancesUseStableIdentitiesAndExplicitRoleSelection()
        {
            DeploymentConfiguration deployment = DeploymentConfiguration.Parse(Bytes(Sample()));
            Assert.Equal(6, deployment.Services.Count);
            ServiceConfiguration gateway = deployment.Select("gateway-2", "gateway");
            Assert.Equal(ServerRole.Gateway, gateway.Role);
            Assert.Equal(18083, gateway.ClientPort);
            Assert.Equal(256 * 1024, gateway.Budgets.MaxFrameBytes);
            Assert.Throws<ArgumentException>(() => deployment.Select("gateway-2", "auth"));
            Assert.Throws<ArgumentException>(() => deployment.Select("worker-1", "game"));
            Assert.Throws<ArgumentException>(() => deployment.Select("gateway-2", "Gateway"));
        }

        [Theory]
        [InlineData("duplicate-id")]
        [InlineData("duplicate-pin")]
        [InlineData("duplicate-port")]
        [InlineData("unknown-role")]
        [InlineData("null-budget")]
        [InlineData("invalid-port")]
        [InlineData("wildcard")]
        [InlineData("mapped-wildcard")]
        [InlineData("missing-client-port")]
        [InlineData("unexpected-client-port")]
        [InlineData("missing-game-version")]
        [InlineData("uppercase-game-digest")]
        [InlineData("second-lobby")]
        [InlineData("insufficient-directory")]
        [InlineData("unknown-field")]
        [InlineData("oversized-frame")]
        [InlineData("too-small-route")]
        [InlineData("too-many-handshakes")]
        public void InvalidDeploymentCannotStart(string problem)
        {
            JsonObject root = Sample();
            JsonArray services = root["services"]!.AsArray();
            switch (problem)
            {
                case "duplicate-id":
                    services[1]!["serviceId"] = "gateway-1";
                    break;
                case "duplicate-pin":
                    services[1]!["certificateSha256"] = new string('1', 64);
                    break;
                case "duplicate-port":
                    services[1]!["clientPort"] = 18082;
                    break;
                case "unknown-role":
                    services[0]!["role"] = "supervisor";
                    break;
                case "null-budget":
                    services[0]!["budgets"] = null;
                    break;
                case "invalid-port":
                    services[2]!["internalPort"] = 65536;
                    break;
                case "wildcard":
                    services[0]!["address"] = "0.0.0.0";
                    break;
                case "mapped-wildcard":
                    services[0]!["address"] = "::ffff:0.0.0.0";
                    break;
                case "missing-client-port":
                    services[0]!.AsObject().Remove("clientPort");
                    break;
                case "unexpected-client-port":
                    services[2]!["clientPort"] = 19999;
                    break;
                case "uppercase-game-digest":
                    services[4]!["configurationDigest"] = new string('A', 64);
                    break;
                case "missing-game-version":
                    services[4]!.AsObject().Remove("gameVersion");
                    break;
                case "second-lobby":
                    services[2]!["role"] = "lobby";
                    break;
                case "insufficient-directory":
                    services[3]!["budgets"] = new JsonObject { ["maxDirectoryEntries"] = 199 };
                    break;
                case "unknown-field":
                    services[0]!["privateKey"] = "should-never-be-here";
                    break;
                case "oversized-frame":
                    services[0]!["budgets"] = new JsonObject { ["maxFrameBytes"] = 262145 };
                    break;
                case "too-small-route":
                    services[0]!["budgets"] = new JsonObject { ["send"] = new JsonObject { ["routeBytes"] = 10 } };
                    break;
                case "too-many-handshakes":
                    services[0]!["budgets"] = new JsonObject { ["maxHandshakes"] = 257 };
                    break;
            }
            Assert.Throws<ArgumentException>(() => DeploymentConfiguration.Parse(Bytes(root)));
        }

        [Theory]
        [InlineData("{\"version\":1,\"version\":1,\"services\":[]}")]
        [InlineData("{\"version\":\"1\",\"services\":[]}")]
        [InlineData("{\"services\":[]}")]
        [InlineData("null")]
        [InlineData("{\"version\":2,\"services\":[]}")]
        public void MalformedOrUnsupportedDocumentIsRejected(string json)
        {
            Assert.Throws<ArgumentException>(() => DeploymentConfiguration.Parse(Encoding.UTF8.GetBytes(json)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void StaticOutgoingConnectionsMustFitCapacity(int serviceIndex)
        {
            JsonObject root = Sample();
            root["services"]![serviceIndex]!["budgets"] = new JsonObject { ["maxConnections"] = 1, ["maxHandshakes"] = 1 };
            ArgumentException error = Assert.Throws<ArgumentException>(() => DeploymentConfiguration.Parse(Bytes(root)));
            Assert.Contains("主动链路", error.Message);
        }

        private static byte[] Bytes(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node);

        internal static JsonObject Sample()
        {
            var services = new JsonArray();
            string[] roles = { "gateway", "gateway", "auth", "lobby", "game", "game" };
            string[] ids = { "gateway-1", "gateway-2", "auth-1", "lobby-1", "worker-1", "worker-2" };
            int[] ports = { 18110, 18111, 18100, 18101, 18201, 18202 };
            for (int index = 0; index < roles.Length; index++)
            {
                var service = new JsonObject
                {
                    ["serviceId"] = ids[index],
                    ["role"] = roles[index],
                    ["address"] = "127.0.0.1",
                    ["certificateSha256"] = new string((char)('1' + index), 64)
                };
                if (roles[index] == "gateway")
                { service["clientPort"] = 18082 + index; }
                else
                { service["internalPort"] = ports[index]; }
                if (roles[index] == "game")
                {
                    service["gameId"] = "grid";
                    service["gameVersion"] = "1";
                    service["configurationDigest"] = new string('a', 64);
                }
                services.Add(service);
            }
            return new JsonObject { ["version"] = 1, ["services"] = services };
        }
    }
}
