using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Server.Actors;
using AiNative.Server.Configuration;
using AiNative.Server.Protocol.Messages;
using AiNative.Server.Protocol.Serialization;
using AiNative.Server.Transport;
using Xunit;

namespace AiNative.Server.Tests.Transport
{
    public sealed class ServerLinkTests
    {
        [Fact]
        public async Task FullMailboxDoesNotBlockNextMessageAndBothDirectionsDrain()
        {
            using Pair pair = await Pair.Create();
            using var worker = new ServerLink(pair.Client, new ServerReceiveContext(pair.Worker, pair.Lobby, pair.Epoch), new ServerBudgets());
            using var lobby = new ServerLink(pair.Server, new ServerReceiveContext(pair.Lobby, pair.Worker, pair.Epoch), new ServerBudgets());
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            int rejected = 0;
            Task workerRun = worker.RunAsync(_ => { reply.TrySetResult(); return PostResult.Accepted; }, (_, _) => throw new Exception("unexpected rejection"));
            Task lobbyRun = lobby.RunAsync(_ =>
            {
                if (++count == 1)
                { return PostResult.Full; }
                received.TrySetResult();
                return PostResult.Accepted;
            }, (_, status) => { Assert.Equal(PostResult.Full, status); rejected++; });
            using JsonDocument payload = JsonDocument.Parse("""{"nonce":"1"}""");
            Assert.Equal(SendAdmission.Accepted, worker.TrySend("ControlHeartbeat", Guid.NewGuid(), payload.RootElement, out Task? first));
            Assert.Equal(SendAdmission.Accepted, worker.TrySend("ControlHeartbeat", Guid.NewGuid(), payload.RootElement, out Task? second));
            await Task.WhenAll(first!, second!, received.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, rejected);
            Assert.Equal(SendAdmission.Accepted, lobby.TrySend("ControlHeartbeatResult", Guid.NewGuid(), payload.RootElement, out Task? response));
            await Task.WhenAll(response!, reply.Task).WaitAsync(TimeSpan.FromSeconds(5));
            worker.CompleteSending();
            await Task.WhenAll(workerRun, lobbyRun).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SendAdmission.Stopped, worker.TrySend("ControlHeartbeat", Guid.NewGuid(), payload.RootElement, out _));
        }

        [Fact]
        public async Task InvalidProtocolClosesAuthenticatedConnection()
        {
            using Pair pair = await Pair.Create();
            using var lobby = new ServerLink(pair.Server, new ServerReceiveContext(pair.Lobby, pair.Worker, pair.Epoch), new ServerBudgets());
            Task run = lobby.RunAsync(_ => throw new Exception("invalid data was delivered"), (_, _) => { });
            var raw = new FramedStream(pair.Client.Stream, 64, new FrameBudget(1, 64), new FrameBudget(1, 64));
            await raw.WriteAsync(Encoding.UTF8.GetBytes("{}"));
            await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(pair.Server.Stream.CanRead);
        }

        [Fact]
        public async Task IdleDeadlineClosesLinkAndStopsAdmission()
        {
            using Pair pair = await Pair.Create();
            using var lobby = new ServerLink(pair.Server, new ServerReceiveContext(pair.Lobby, pair.Worker, pair.Epoch), new ServerBudgets { ReadSeconds = 1 });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lobby.RunAsync(_ => PostResult.Accepted, (_, _) => { }).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(pair.Server.Stream.CanRead);
        }

        [Fact]
        public async Task LinkRequiresConfirmedEpochAndCannotKeepRegistrationBypass()
        {
            using Pair pair = await Pair.Create();
            Assert.Throws<ArgumentException>(() => new ServerLink(pair.Server,
                new ServerReceiveContext(pair.Lobby, pair.Worker, pair.Epoch, AllowRegistration: true), new ServerBudgets()));
            Assert.Throws<ArgumentException>(() => new ServerLink(pair.Server,
                new ServerReceiveContext(pair.Lobby, pair.Worker, null), new ServerBudgets()));
            Assert.True(pair.Server.Stream.CanRead);
        }

        private sealed class Pair : IDisposable
        {
            private readonly TcpClient _client;
            private readonly TcpClient _server;
            internal readonly AuthenticatedServerStream Client;
            internal readonly AuthenticatedServerStream Server;
            internal readonly ServerPeer Worker;
            internal readonly ServerPeer Lobby;
            internal readonly Guid Epoch;
            private Pair(TcpClient client, TcpClient server, AuthenticatedServerStream outgoing, AuthenticatedServerStream incoming,
                ServerPeer worker, ServerPeer lobby, Guid epoch)
            {
                _client = client;
                _server = server;
                Client = outgoing;
                Server = incoming;
                Worker = worker;
                Lobby = lobby;
                Epoch = epoch;
            }
            internal static async Task<Pair> Create()
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var clientCertificate = AuthenticatedServerStreamTests.Certificate("worker-1");
                using var serverCertificate = AuthenticatedServerStreamTests.Certificate("lobby-1");
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var client = new TcpClient { NoDelay = true };
                Task connecting = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token).AsTask();
                TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
                server.NoDelay = true;
                await connecting;
                var worker = new ServerPeer("worker-1", Guid.NewGuid(), ServerRole.Game);
                var lobby = new ServerPeer("lobby-1", Guid.NewGuid(), ServerRole.Lobby);
                Guid epoch = Guid.NewGuid();
                Task<AuthenticatedServerStream> accept = AuthenticatedServerStream.AcceptAsync(server.GetStream(), serverCertificate,
                    new ServerHello(lobby, epoch), new[] { new ServerCertificateIdentity(worker.ServiceId, worker.Role, clientCertificate.GetCertHashString(HashAlgorithmName.SHA256)) }, TimeSpan.FromSeconds(5), deadline.Token);
                AuthenticatedServerStream outgoing = await AuthenticatedServerStream.ConnectAsync(client.GetStream(), clientCertificate,
                    new ServerHello(worker, epoch), new ServerCertificateIdentity(lobby.ServiceId, lobby.Role, serverCertificate.GetCertHashString(HashAlgorithmName.SHA256)), TimeSpan.FromSeconds(5), deadline.Token);
                return new Pair(client, server, outgoing, await accept, worker, lobby, epoch);
            }
            public void Dispose() { Client.Dispose(); Server.Dispose(); _client.Dispose(); _server.Dispose(); }
        }
    }
}
