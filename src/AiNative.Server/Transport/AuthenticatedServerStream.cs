using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Protocol.Serialization;

namespace AiNative.Server.Transport
{
    /// <summary>拥有底层流的双向 TLS 链路。工厂失败同样关闭流；证书由调用方拥有，需活到握手结束。</summary>
    public sealed class AuthenticatedServerStream : IDisposable
    {
        private static readonly SslApplicationProtocol ApplicationProtocol = new SslApplicationProtocol("ainative-server/1");
        private readonly SslStream _stream;

        private AuthenticatedServerStream(SslStream stream, ServerHello remote)
        {
            _stream = stream;
            Remote = remote;
        }

        public ServerHello Remote { get; }

        /// <summary>仅连接宿主可读写此流，不能与其他分帧器并发使用。Dispose 关闭整个连接。</summary>
        public Stream Stream => _stream;

        public static Task<AuthenticatedServerStream> ConnectAsync(Stream transport, X509Certificate2 certificate,
            ServerHello local, ServerCertificateIdentity expectedRemote, TimeSpan handshakeTimeout, CancellationToken cancellationToken = default) =>
            AuthenticateAsync(transport, certificate, local, new[] { expectedRemote }, expectedRemote.ServiceId, handshakeTimeout, cancellationToken);

        public static Task<AuthenticatedServerStream> AcceptAsync(Stream transport, X509Certificate2 certificate,
            ServerHello local, IReadOnlyList<ServerCertificateIdentity> allowedRemotes, TimeSpan handshakeTimeout, CancellationToken cancellationToken = default) =>
            AuthenticateAsync(transport, certificate, local, allowedRemotes, null, handshakeTimeout, cancellationToken);

        public void Dispose() => _stream.Dispose();

        private static async Task<AuthenticatedServerStream> AuthenticateAsync(Stream transport, X509Certificate2 certificate,
            ServerHello local, IReadOnlyList<ServerCertificateIdentity> allowedRemotes, string? targetHost,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transport);
            var identities = new List<ServerCertificateIdentity>(allowedRemotes);
            ServerCertificateIdentity? authenticated = null;
            var stream = new SslStream(transport, false, (_, remoteCertificate, _, _) =>
            {
                authenticated = FindIdentity(remoteCertificate, identities, targetHost == null);
                return authenticated != null;
            });
            try
            {
                if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
                byte[] hello = ServerHelloCodec.Encode(local);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);
                if (targetHost == null)
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        AllowRenegotiation = false,
                        ApplicationProtocols = new List<SslApplicationProtocol> { ApplicationProtocol }
                    }, deadline.Token).ConfigureAwait(false);
                }
                else
                {
                    await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = targetHost,
                        ClientCertificates = new X509CertificateCollection { certificate },
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        AllowRenegotiation = false,
                        ApplicationProtocols = new List<SslApplicationProtocol> { ApplicationProtocol }
                    }, deadline.Token).ConfigureAwait(false);
                }
                if (authenticated == null || !stream.IsMutuallyAuthenticated || stream.NegotiatedApplicationProtocol != ApplicationProtocol)
                {
                    throw new AuthenticationException("内部链路未完成双向身份认证或协议协商。");
                }
                var frames = new FramedStream(stream, ServerHelloCodec.MaximumBytes,
                    new FrameBudget(1, ServerHelloCodec.MaximumBytes), new FrameBudget(1, ServerHelloCodec.MaximumBytes));
                await frames.WriteAsync(hello, deadline.Token).ConfigureAwait(false);
                using ReceivedFrame? received = await frames.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (received == null || !ServerHelloCodec.TryDecode(received.Body, authenticated.ServiceId, authenticated.Role, out ServerHello? remote))
                {
                    throw new AuthenticationException("链路握手与证书身份不匹配。");
                }
                return new AuthenticatedServerStream(stream, remote!);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // 精确 pin 取代操作系统根链和 DNS 名称信任；仍要求有效期、用途及 TLS 私钥持有证明。
        private static ServerCertificateIdentity? FindIdentity(X509Certificate? certificate,
            List<ServerCertificateIdentity> identities, bool remoteIsClient)
        {
            if (certificate == null) { return null; }
            using var copy = new X509Certificate2(certificate);
            DateTime now = DateTime.UtcNow;
            if (now < copy.NotBefore.ToUniversalTime() || now > copy.NotAfter.ToUniversalTime()) { return null; }
            string requiredUsage = remoteIsClient ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1";
            bool usageAllowed = false;
            foreach (X509Extension extension in copy.Extensions)
            {
                if (extension is X509EnhancedKeyUsageExtension usage)
                {
                    foreach (Oid oid in usage.EnhancedKeyUsages)
                    {
                        if (oid.Value == requiredUsage) { usageAllowed = true; }
                    }
                }
            }
            if (!usageAllowed) { return null; }
            string fingerprint = copy.GetCertHashString(HashAlgorithmName.SHA256);
            ServerCertificateIdentity? match = null;
            foreach (ServerCertificateIdentity identity in identities)
            {
                if (string.Equals(identity.Sha256Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    if (match != null) { return null; }
                    match = identity;
                }
            }
            return match;
        }
    }
}
