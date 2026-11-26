using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Actors;
using AiNative.Server.Configuration;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Protocol.Serialization;
using AiNative.Server.Transport;

namespace AiNative.Server.Lifecycle
{
    public sealed record HostStatus(string ServiceId, Guid BootId, RoleState State, bool Ready, string Event);

    /// <summary>S1 角色传输宿主。建立真实认证链路与心跳；业务 Actor 在 S3/S4 装配前始终不报告 Ready。</summary>
    public sealed class RoleHost
    {
        private readonly DeploymentConfiguration _deployment;
        private readonly ServiceConfiguration _service;
        private readonly X509Certificate2 _certificate;
        private readonly Action<HostStatus> _report;
        private readonly RoleLifetime _lifetime = new RoleLifetime();
        private readonly ServerPeer _local;
        private readonly ConcurrentDictionary<long, ServerLink> _links = new ConcurrentDictionary<long, ServerLink>();
        private readonly ConcurrentDictionary<long, Task> _connections = new ConcurrentDictionary<long, Task>();
        private readonly ConcurrentDictionary<string, ServerLink> _peerLinks = new ConcurrentDictionary<string, ServerLink>(StringComparer.Ordinal);
        private long _connectionSequence;
        private Guid? _epoch;
        private int _started;
        private int _stopping;
        private readonly object _admissionGate = new object();
        private CancellationTokenSource? _admissionStop;

        public RoleHost(DeploymentConfiguration deployment, ServiceConfiguration service, X509Certificate2 certificate, Action<HostStatus> report)
        {
            ArgumentNullException.ThrowIfNull(deployment);
            ArgumentNullException.ThrowIfNull(service);
            ArgumentNullException.ThrowIfNull(certificate);
            ArgumentNullException.ThrowIfNull(report);
            if (service.Role == ServerRole.Game || !deployment.Services.Contains(service)) { throw new ArgumentException("通用 Host 仅启动清单中的 gateway/auth/lobby。"); }
            if (!certificate.HasPrivateKey || !string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), service.Certificate.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("本地证书必须含私钥且匹配部署身份。"); }
            _deployment = deployment;
            _service = service;
            _certificate = certificate;
            _report = report;
            _local = new ServerPeer(service.ServiceId, Guid.NewGuid(), service.Role);
            if (service.Role == ServerRole.Lobby) { _epoch = Guid.NewGuid(); }
        }

        public RoleState State => _lifetime.State;
        public void BeginDrain()
        {
            _lifetime.BeginDrain();
            lock (_admissionGate) { _admissionStop?.Cancel(); }
            Report("draining");
        }

        public async Task<RoleStopResult> RunAsync(CancellationToken stop)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) { throw new InvalidOperationException("角色宿主只能启动一次。"); }
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_admissionGate) { _admissionStop = admission; }
            using var connections = new CancellationTokenSource();
            using var capacity = new SemaphoreSlim(_service.Budgets.MaxConnections);
            using var handshakes = new SemaphoreSlim(_service.Budgets.MaxHandshakes);
            TcpListener? listener = null;
            Task accepting = Task.CompletedTask;
            _lifetime.Register("connections", async deadline =>
            {
                Interlocked.Exchange(ref _stopping, 1);
                foreach (ServerLink link in _links.Values) { link.CompleteSending(); }
                using CancellationTokenRegistration abort = deadline.Register(() =>
                {
                    connections.Cancel();
                    foreach (ServerLink link in _links.Values) { link.Dispose(); }
                });
                await Task.WhenAll(_connections.Values).WaitAsync(deadline).ConfigureAwait(false);
                connections.Cancel();
            });
            _lifetime.Register("admission", async deadline =>
            {
                admission.Cancel();
                listener?.Stop();
                try { await accepting.WaitAsync(deadline).ConfigureAwait(false); }
                catch (OperationCanceledException) when (admission.IsCancellationRequested) { }
            });
            try
            {
                foreach (ServiceConfiguration peer in Outgoing().Where(peer => peer.Role == ServerRole.Lobby))
                {
                    await capacity.WaitAsync(admission.Token).ConfigureAwait(false);
                    var client = new TcpClient { NoDelay = true };
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(_service.Budgets.HandshakeSeconds));
                        await client.ConnectAsync(peer.InternalEndpoint!, deadline.Token).ConfigureAwait(false);
                        AuthenticatedServerStream stream = await AuthenticatedServerStream.ConnectAsync(client.GetStream(), _certificate,
                            new ServerHello(_local, _epoch), peer.Certificate, TimeSpan.FromSeconds(_service.Budgets.HandshakeSeconds), deadline.Token).ConfigureAwait(false);
                        if (peer.Role == ServerRole.Lobby && _epoch == null) { _epoch = stream.Remote.ControlEpoch; }
                        Track(client, stream, capacity, connections.Token);
                    }
                    catch { client.Dispose(); capacity.Release(); throw; }
                }
                foreach (ServiceConfiguration peer in Outgoing().Where(peer => peer.Role != ServerRole.Lobby))
                {
                    StartRetryingPeer(peer, capacity, handshakes, admission.Token, connections.Token);
                }
                if (_service.InternalEndpoint != null)
                {
                    listener = new TcpListener(_service.InternalEndpoint);
                    listener.Start();
                    accepting = AcceptLoopAsync(listener, capacity, handshakes, admission.Token, connections.Token);
                }
                _lifetime.MarkLive();
                Report("live");
                Task stopping = Task.Delay(Timeout.Infinite, admission.Token);
                if (listener == null) { await stopping.ConfigureAwait(false); }
                else { await await Task.WhenAny(accepting, stopping).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested) { }
            catch (Exception)
            {
                _lifetime.ReportCriticalFailure();
                Report("startup-or-admission-failed");
            }
            finally { BeginDrain(); }
            RoleStopResult result = await _lifetime.StopAsync(TimeSpan.FromSeconds(_service.Budgets.ShutdownSeconds)).ConfigureAwait(false);
            Report(result.Clean ? "stopped" : "unclean-stop");
            lock (_admissionGate) { _admissionStop = null; }
            return result;
        }

        private IEnumerable<ServiceConfiguration> Outgoing()
        {
            if (_service.Role == ServerRole.Lobby) { return Array.Empty<ServiceConfiguration>(); }
            return _deployment.Services.Where(peer => peer.Role == ServerRole.Lobby || peer.Role == ServerRole.Game ||
                (_service.Role == ServerRole.Gateway && peer.Role == ServerRole.Auth)).OrderBy(peer => peer.Role == ServerRole.Lobby ? 0 : 1).ThenBy(peer => peer.ServiceId, StringComparer.Ordinal);
        }

        private void StartRetryingPeer(ServiceConfiguration peer, SemaphoreSlim capacity, SemaphoreSlim handshakes,
            CancellationToken admission, CancellationToken connections)
        {
            long id = Interlocked.Increment(ref _connectionSequence);
            var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = RetryPeerAsync(begin.Task, id, peer, capacity, handshakes, admission, connections);
            _connections[id] = task;
            begin.SetResult();
        }

        private async Task RetryPeerAsync(Task begin, long id, ServiceConfiguration peer, SemaphoreSlim capacity,
            SemaphoreSlim handshakes, CancellationToken admission, CancellationToken connections)
        {
            await begin.ConfigureAwait(false);
            int failures = 0;
            try
            {
                while (!admission.IsCancellationRequested)
                {
                    await capacity.WaitAsync(admission).ConfigureAwait(false);
                    using var client = new TcpClient { NoDelay = true };
                    try
                    {
                        await handshakes.WaitAsync(admission).ConfigureAwait(false);
                        AuthenticatedServerStream stream;
                        try
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(admission);
                            deadline.CancelAfter(TimeSpan.FromSeconds(_service.Budgets.HandshakeSeconds));
                            await client.ConnectAsync(peer.InternalEndpoint!, deadline.Token).ConfigureAwait(false);
                            stream = await AuthenticatedServerStream.ConnectAsync(client.GetStream(), _certificate,
                                new ServerHello(_local, _epoch), peer.Certificate, TimeSpan.FromSeconds(_service.Budgets.HandshakeSeconds), deadline.Token).ConfigureAwait(false);
                        }
                        finally { handshakes.Release(); }
                        failures = 0;
                        await RunConnectionAsync(id, stream, connections).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (admission.IsCancellationRequested) { break; }
                    catch (Exception) { Report("peer-unavailable"); }
                    finally { capacity.Release(); }
                    int ceiling = Math.Min(5000, 250 << failures);
                    failures = Math.Min(failures + 1, 4);
                    await Task.Delay(Random.Shared.Next(ceiling / 2, ceiling + 1), admission).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested) { }
            finally { _connections.TryRemove(id, out _); }
        }

        private async Task AcceptLoopAsync(TcpListener listener, SemaphoreSlim capacity, SemaphoreSlim handshakes,
            CancellationToken admission, CancellationToken connections)
        {
            while (!admission.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(admission).ConfigureAwait(false);
                client.NoDelay = true;
                if (!capacity.Wait(0)) { client.Dispose(); continue; }
                if (!handshakes.Wait(0)) { capacity.Release(); client.Dispose(); continue; }
                long id = Interlocked.Increment(ref _connectionSequence);
                var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task task = AuthenticateIncomingAsync(begin.Task, id, client, capacity, handshakes, connections);
                _connections[id] = task;
                begin.SetResult();
            }
        }

        private async Task AuthenticateIncomingAsync(Task begin, long id, TcpClient client, SemaphoreSlim capacity,
            SemaphoreSlim handshakes, CancellationToken cancellationToken)
        {
            await begin.ConfigureAwait(false);
            try
            {
                ServerCertificateIdentity[] allowed = _deployment.Services.Where(peer =>
                    _service.Role == ServerRole.Lobby ? peer.Role != ServerRole.Lobby : peer.Role == ServerRole.Gateway).Select(peer => peer.Certificate).ToArray();
                AuthenticatedServerStream stream;
                try
                {
                    stream = await AuthenticatedServerStream.AcceptAsync(client.GetStream(), _certificate, new ServerHello(_local, _epoch),
                        allowed, TimeSpan.FromSeconds(_service.Budgets.HandshakeSeconds), cancellationToken).ConfigureAwait(false);
                }
                finally { handshakes.Release(); }
                await RunConnectionAsync(id, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) { Report("connection-closed"); }
            finally { client.Dispose(); capacity.Release(); _connections.TryRemove(id, out _); }
        }

        private void Track(TcpClient client, AuthenticatedServerStream stream, SemaphoreSlim capacity, CancellationToken cancellationToken)
        {
            long id = Interlocked.Increment(ref _connectionSequence);
            var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = RunOutgoingAsync(begin.Task, id, client, stream, capacity, cancellationToken);
            _connections[id] = task;
            begin.SetResult();
        }

        private async Task RunOutgoingAsync(Task begin, long id, TcpClient client, AuthenticatedServerStream stream,
            SemaphoreSlim capacity, CancellationToken cancellationToken)
        {
            await begin.ConfigureAwait(false);
            try { await RunConnectionAsync(id, stream, cancellationToken).ConfigureAwait(false); }
            catch (Exception) { Report("connection-closed"); }
            finally
            {
                client.Dispose(); capacity.Release(); _connections.TryRemove(id, out _);
                if (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _stopping) == 0 && stream.Remote.Peer.Role == ServerRole.Lobby)
                {
                    _lifetime.ReportCriticalFailure();
                    BeginDrain();
                }
            }
        }

        private async Task RunConnectionAsync(long id, AuthenticatedServerStream stream, CancellationToken cancellationToken)
        {
            using (stream)
            {
                if (Volatile.Read(ref _stopping) != 0) { return; }
                if (_epoch == null || (stream.Remote.ControlEpoch != null && stream.Remote.ControlEpoch != _epoch))
                { throw new AuthenticationException("控制 epoch 不一致。"); }
                using var link = new ServerLink(stream, new ServerReceiveContext(_local, stream.Remote.Peer, _epoch), _service.Budgets);
                if (!_peerLinks.TryAdd(stream.Remote.Peer.ServiceId, link)) { throw new AuthenticationException("同一对端已有活动链路。"); }
                _links[id] = link;
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task run = link.RunAsync(message =>
                {
                    if (message.Type == "LinkPing")
                    {
                        SendAdmission result = link.TrySend("LinkPong", message.OperationId, message.Payload, out Task? written);
                        if (written != null) { _ = ObserveSendAsync(written); }
                        return result == SendAdmission.Accepted ? PostResult.Accepted : PostResult.Stopped;
                    }
                    return message.Type == "LinkPong" ? PostResult.Accepted : PostResult.Stopped;
                }, (_, _) => Report("business-not-ready"), heartbeatStop.Token);
                Task heartbeat = HeartbeatAsync(link, heartbeatStop.Token);
                try { await run.ConfigureAwait(false); }
                finally
                {
                    heartbeatStop.Cancel();
                    try { await heartbeat.ConfigureAwait(false); } catch (Exception) { }
                    _links.TryRemove(id, out _);
                    _peerLinks.TryRemove(stream.Remote.Peer.ServiceId, out _);
                }
            }
        }

        private static async Task HeartbeatAsync(ServerLink link, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            long sequence = 0;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                JsonElement payload = JsonSerializer.SerializeToElement(new { nonce = checked(++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture) });
                if (link.TrySend("LinkPing", Guid.NewGuid(), payload, out Task? written) != SendAdmission.Accepted) { return; }
                await written!.ConfigureAwait(false);
            }
        }
        private static async Task ObserveSendAsync(Task written) { try { await written.ConfigureAwait(false); } catch (Exception) { } }
        private void Report(string name)
        {
            try { _report(new HostStatus(_local.ServiceId, _local.BootId, _lifetime.State, false, name)); }
            catch (Exception) { _lifetime.ReportCriticalFailure(); }
        }
    }
}
