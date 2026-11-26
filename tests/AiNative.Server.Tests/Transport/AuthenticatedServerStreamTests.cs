using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Protocol.Serialization;
using AiNative.Server.Transport;
using Xunit;

namespace AiNative.Server.Tests.Transport
{
    public sealed class AuthenticatedServerStreamTests
    {
        [Fact]
        public async Task MutualTlsBindsCertificateIdentityAndSupportsFramedTraffic()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var serverCertificate = Certificate("lobby-1");
            using var clientCertificate = Certificate("worker-1");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            Task connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            await connecting;
            var clientHello = new ServerHello(new ServerPeer("worker-1", Guid.NewGuid(), ServerRole.Game), null);
            var serverHello = new ServerHello(new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby), Guid.NewGuid());
            Task<AuthenticatedServerStream> accept = AuthenticatedServerStream.AcceptAsync(server.GetStream(), serverCertificate,
                serverHello, new[] { Identity(clientCertificate, clientHello.Peer) }, TimeSpan.FromSeconds(5), deadline.Token);
            using AuthenticatedServerStream outgoing = await AuthenticatedServerStream.ConnectAsync(client.GetStream(), clientCertificate,
                clientHello, Identity(serverCertificate, serverHello.Peer), TimeSpan.FromSeconds(5), deadline.Token);
            using AuthenticatedServerStream incoming = await accept;
            Assert.Equal(clientHello, incoming.Remote);
            Assert.Equal(serverHello, outgoing.Remote);
            var sender = new FramedStream(outgoing.Stream, 64, new FrameBudget(1, 64), new FrameBudget(1, 64));
            var receiver = new FramedStream(incoming.Stream, 64, new FrameBudget(1, 64), new FrameBudget(1, 64));
            await sender.WriteAsync(new byte[] { 10, 20, 30 }, deadline.Token);
            using ReceivedFrame received = Assert.IsType<ReceivedFrame>(await receiver.ReadAsync(deadline.Token));
            Assert.Equal(new byte[] { 10, 20, 30 }, received.Body.ToArray());
        }

        [Theory]
        [InlineData("unknown-certificate")]
        [InlineData("server-certificate")]
        [InlineData("wrong-service")]
        [InlineData("wrong-role")]
        [InlineData("expired")]
        [InlineData("wrong-usage")]
        public async Task UntrustedOrMisrepresentedPeerCannotCompleteLink(string failure)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var serverCertificate = Certificate("lobby-1");
            using var clientCertificate = Certificate("worker-1", failure == "expired", failure != "wrong-usage");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            Task connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            await connecting;
            var worker = new ServerPeer("worker-1", Guid.NewGuid(), ServerRole.Game);
            var lobby = new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby);
            ServerCertificateIdentity allowed = Identity(clientCertificate, worker);
            if (failure == "unknown-certificate")
            { allowed = allowed with { Sha256Fingerprint = new string('0', 64) }; }
            ServerPeer claimed = failure == "wrong-service" ? worker with { ServiceId = "worker-2" } :
                failure == "wrong-role" ? worker with { Role = ServerRole.Auth } : worker;
            Task<AuthenticatedServerStream> accept = AuthenticatedServerStream.AcceptAsync(server.GetStream(), serverCertificate,
                new ServerHello(lobby, Guid.NewGuid()), new[] { allowed }, TimeSpan.FromSeconds(5), deadline.Token);
            Task<AuthenticatedServerStream> connect = AuthenticatedServerStream.ConnectAsync(client.GetStream(), clientCertificate,
                new ServerHello(claimed, null), failure == "server-certificate" ? Identity(serverCertificate, lobby) with { Sha256Fingerprint = new string('0', 64) } : Identity(serverCertificate, lobby), TimeSpan.FromSeconds(5), deadline.Token);
            Exception? failureResult = await Record.ExceptionAsync(async () =>
            {
                await Task.WhenAll(accept, connect);
            });
            if (connect.IsCompletedSuccessfully)
            { (await connect).Dispose(); }
            if (accept.IsCompletedSuccessfully)
            { (await accept).Dispose(); }
            Assert.NotNull(failureResult);
            Assert.True(failureResult is AuthenticationException || failureResult is IOException, failureResult.ToString());
            Assert.True(accept.IsFaulted);
        }

        [Fact]
        public async Task SilentPeerTimesOutAndClosesOwnedStream()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var certificate = Certificate("lobby-1");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            Task connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            await connecting;
            NetworkStream stream = server.GetStream();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthenticatedServerStream.AcceptAsync(stream, certificate,
                new ServerHello(new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby), Guid.NewGuid()),
                Array.Empty<ServerCertificateIdentity>(), TimeSpan.FromMilliseconds(100), deadline.Token));
            Assert.False(stream.CanRead);
        }

        [Fact]
        public async Task TlsPeerThatNeverSendsHelloUsesSameHandshakeDeadline()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var certificate = Certificate("lobby-1");
            using var clientCertificate = Certificate("worker-1");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            Task connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
            using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
            await connecting;
            NetworkStream owned = server.GetStream();
            var worker = new ServerPeer("worker-1", Guid.NewGuid(), ServerRole.Game);
            Task<AuthenticatedServerStream> accepting = AuthenticatedServerStream.AcceptAsync(owned, certificate,
                new ServerHello(new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby), Guid.NewGuid()),
                new[] { Identity(clientCertificate, worker) }, TimeSpan.FromSeconds(1), deadline.Token);
            // 此夹具仅完成 TLS，故意不执行应用握手。
            using var rawPeer = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await rawPeer.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "lobby-1",
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("ainative-server/1") },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, deadline.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepting);
            Assert.False(owned.CanRead);
        }

        private static ServerCertificateIdentity Identity(X509Certificate2 certificate, ServerPeer peer) =>
            new ServerCertificateIdentity(peer.ServiceId, peer.Role, certificate.GetCertHashString(HashAlgorithmName.SHA256));

        internal static X509Certificate2 Certificate(string name, bool expired = false, bool clientUsage = true)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
            if (clientUsage)
            { usages.Add(new Oid("1.3.6.1.5.5.7.3.2")); }
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }
    }
}
