using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiNative.Server.Protocol.Messages;

namespace AiNative.Server.Configuration
{
    /// <summary>不读文件的部署清单校验器。清单不包含密码、证书私钥、PID 或 bootId。</summary>
    public sealed class DeploymentConfiguration
    {
        private sealed class DeploymentInput
        {
            public required int Version { get; init; }
            public required ServiceInput[] Services { get; init; }
        }
        private sealed class ServiceInput
        {
            public required string ServiceId { get; init; }
            public required string Role { get; init; }
            public required string Address { get; init; }
            public int? InternalPort { get; init; }
            public int? ClientPort { get; init; }
            public required string CertificateSha256 { get; init; }
            public string? GameId { get; init; }
            public string? GameVersion { get; init; }
            public string? ConfigurationDigest { get; init; }
            public ServerBudgets Budgets { get; init; } = new ServerBudgets();
        }
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16
        };
        private DeploymentConfiguration(List<ServiceConfiguration> services) { Services = services.AsReadOnly(); }
        public ReadOnlyCollection<ServiceConfiguration> Services { get; }

        public ServiceConfiguration Select(string serviceId, string role)
        {
            ServerRole requestedRole = ParseRole(role);
            if (requestedRole == ServerRole.Game) { throw new ArgumentException("Game 角色只能由显式装配游戏的 GameServer 入口启动。"); }
            foreach (ServiceConfiguration service in Services)
            {
                if (service.ServiceId == serviceId && service.Role == requestedRole) { return service; }
            }
            throw new ArgumentException("serviceId 与角色不匹配部署清单。");
        }

        public static DeploymentConfiguration Parse(ReadOnlyMemory<byte> utf8)
        {
            if (utf8.Length == 0 || utf8.Length > 1024 * 1024) { throw new ArgumentException("部署清单大小无效。"); }
            DeploymentInput input;
            try
            {
                new UTF8Encoding(false, true).GetCharCount(utf8.Span);
                using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 16 });
                CheckUnique(document.RootElement);
                input = document.RootElement.Deserialize<DeploymentInput>(Options) ?? throw new ArgumentException("部署清单不能为空。");
            }
            catch (JsonException) { throw new ArgumentException("部署清单 JSON 字段、类型或结构无效。"); }
            catch (DecoderFallbackException) { throw new ArgumentException("部署清单必须是有效 UTF-8。"); }
            catch (InvalidOperationException) { throw new ArgumentException("部署清单包含无效字符串。"); }
            if (input.Version != 1 || input.Services == null || input.Services.Length == 0 || input.Services.Length > 256)
            { throw new ArgumentException("部署版本或实例数量无效。"); }
            var services = new List<ServiceConfiguration>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            int authCount = 0;
            int lobbyCount = 0;
            int gatewayCount = 0;
            long rooms = 0;
            ServiceConfiguration? lobby = null;
            foreach (ServiceInput item in input.Services)
            {
                if (item == null || !Identifier(item.ServiceId) || !ids.Add(item.ServiceId) || !Hex(item.CertificateSha256) || !pins.Add(item.CertificateSha256) ||
                    !IPAddress.TryParse(item.Address, out IPAddress? address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                { throw new ArgumentException("服务标识、证书身份或监听地址无效/重复。"); }
                ServerRole role = ParseRole(item.Role);
                if ((role != ServerRole.Gateway) != item.InternalPort.HasValue) { throw new ArgumentException("Auth/Lobby/Game 必须配置内部监听；Gateway 仅主动建立内部链路。"); }
                if (address.IsIPv4MappedToIPv6) { address = address.MapToIPv4(); }
                if (address.Equals(IPAddress.Any)) { throw new ArgumentException("监听必须指定明确地址。"); }
                if (item.InternalPort.HasValue) { CheckEndpoint(address, item.InternalPort.Value, endpoints); }
                if ((role == ServerRole.Gateway) != item.ClientPort.HasValue) { throw new ArgumentException("只有 Gateway 必须配置客户端端口。"); }
                if (item.ClientPort.HasValue) { CheckEndpoint(address, item.ClientPort.Value, endpoints); }
                if (item.Budgets == null) { throw new ArgumentException("预算不能为空。"); }
                item.Budgets.Validate();
                bool hasGame = Identifier(item.GameId) && Identifier(item.GameVersion) && Hex(item.ConfigurationDigest) &&
                    item.ConfigurationDigest == item.ConfigurationDigest!.ToLowerInvariant();
                if (role == ServerRole.Game ? !hasGame : item.GameId != null || item.GameVersion != null || item.ConfigurationDigest != null)
                { throw new ArgumentException("只有 Game 必须配置游戏版本与配置摘要。"); }
                var service = new ServiceConfiguration(item.ServiceId, role, address, item.InternalPort, item.ClientPort,
                    item.CertificateSha256, item.GameId, item.GameVersion, item.ConfigurationDigest, item.Budgets);
                services.Add(service);
                if (role == ServerRole.Auth) { authCount++; }
                if (role == ServerRole.Lobby) { lobbyCount++; lobby = service; }
                if (role == ServerRole.Gateway) { gatewayCount++; }
                if (role == ServerRole.Game) { rooms += item.Budgets.MaxRooms; }
            }
            if (authCount != 1 || lobbyCount != 1 || gatewayCount == 0 || rooms == 0 || lobby!.Budgets.MaxDirectoryEntries < rooms)
            { throw new ArgumentException("首期必须有唯一 Auth/Lobby、Gateway 和 Game，目录预算须覆盖 Worker 房间总量。"); }
            int workerCount = 0;
            foreach (ServiceConfiguration service in services) { if (service.Role == ServerRole.Game) { workerCount++; } }
            foreach (ServiceConfiguration service in services)
            {
                int outgoing = service.Role == ServerRole.Gateway ? workerCount + 2 : service.Role == ServerRole.Auth ? workerCount + 1 : 0;
                if (service.Budgets.MaxConnections < outgoing) { throw new ArgumentException("连接额度必须容纳静态部署的全部主动链路。"); }
            }
            return new DeploymentConfiguration(services);
        }

        private static ServerRole ParseRole(string? role) => role switch
        {
            "gateway" => ServerRole.Gateway, "auth" => ServerRole.Auth, "lobby" => ServerRole.Lobby, "game" => ServerRole.Game,
            _ => throw new ArgumentException("角色只能为 gateway/auth/lobby/game。")
        };
        private static bool Identifier(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) { return false; }
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_' && character != '.') { return false; }
            }
            return true;
        }
        private static bool Hex(string? value)
        {
            if (value == null || value.Length != 64) { return false; }
            foreach (char character in value) { if (!Uri.IsHexDigit(character)) { return false; } }
            return true;
        }
        private static void CheckEndpoint(IPAddress address, int port, HashSet<string> endpoints)
        {
            if (port < 1 || port > 65535 || !endpoints.Add(new IPEndPoint(address, port).ToString()))
            { throw new ArgumentException("端口无效或监听端点重复。"); }
        }
        private static void CheckUnique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) { throw new ArgumentException("部署清单包含重复字段。"); }
                    CheckUnique(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray()) { CheckUnique(item); }
            }
            else if (value.ValueKind == JsonValueKind.String) { _ = value.GetString(); }
        }
    }
}
