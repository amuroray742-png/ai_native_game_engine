using System.Net;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Transport;

namespace AiNative.Server.Configuration
{
    public sealed class ServiceConfiguration
    {
        internal ServiceConfiguration(string serviceId, ServerRole role, IPAddress address, int? internalPort,
            int? clientPort, string fingerprint, string? gameId, string? gameVersion, string? configurationDigest, ServerBudgets budgets)
        {
            ServiceId = serviceId;
            Role = role;
            Address = address.ToString();
            InternalPort = internalPort;
            ClientPort = clientPort;
            Certificate = new ServerCertificateIdentity(serviceId, role, fingerprint);
            GameId = gameId;
            GameVersion = gameVersion;
            ConfigurationDigest = configurationDigest;
            Budgets = budgets;
        }
        public string ServiceId { get; }
        public ServerRole Role { get; }
        public string Address { get; }
        public int? InternalPort { get; }
        public int? ClientPort { get; }
        public ServerCertificateIdentity Certificate { get; }
        public string? GameId { get; }
        public string? GameVersion { get; }
        public string? ConfigurationDigest { get; }
        public ServerBudgets Budgets { get; }
        public IPEndPoint? InternalEndpoint => InternalPort.HasValue ? new IPEndPoint(IPAddress.Parse(Address), InternalPort.Value) : null;
    }
}
