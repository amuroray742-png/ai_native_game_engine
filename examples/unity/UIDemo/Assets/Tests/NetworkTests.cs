using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AiNative.UnityCore;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using AiNative.UnityCore.Network;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AiNative.NetworkDemo.Tests
{
    public sealed class NetworkTests
    {
        private Core _core;
        private NetworkModule _network;
        private readonly ShutdownConsumer _consumer = new ShutdownConsumer();
        private CancellationTokenSource _deadline;
        private int TcpPort => int.Parse(Environment.GetEnvironmentVariable("AINATIVE_NETWORK_TCP") ?? "18081");
        private Uri UriFor(string path) => new Uri("http://127.0.0.1:" +
            (Environment.GetEnvironmentVariable("AINATIVE_NETWORK_HTTP") ?? "18080") + path);

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            Core.ResetForPlay();
            _core = Core.Inst;
            _network = new NetworkModule();
            _consumer.Pending = UniTask.CompletedTask;
            _core.RegisterModule(_network).RegisterModule(_consumer);
            await _core.RunAsync();
            _deadline = new CancellationTokenSource();
            _deadline.CancelAfterSlim(TimeSpan.FromSeconds(15), DelayType.Realtime);
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            await _core.CloseAsync();
            _deadline.Cancel();
            _deadline.Dispose();
            Core.ResetForPlay();
        });

        [UnityTest]
        public IEnumerator HttpPreservesBinaryHeadersAndErrorResponses() => UniTask.ToCoroutine(async () =>
        {
            var bytes = new byte[] { 0, 255, 42 };
            var response = await _network.SendAsync(new HttpRequest("POST", UriFor("/echo"))
            {
                Headers = new Dictionary<string, string> { { "X-Demo", "binary" } },
                Body = bytes
            }, _deadline.Token);
            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("loopback", response.Headers["X-Fixture"]);
            CollectionAssert.AreEqual(bytes, response.Body);
            foreach (var pair in new[] { ("/missing", 404), ("/error", 503) })
            {
                var failure = await _network.SendAsync(new HttpRequest("GET", UriFor(pair.Item1)), _deadline.Token);
                Assert.AreEqual(pair.Item2, failure.StatusCode);
                Assert.AreEqual("fixture-error", Encoding.UTF8.GetString(failure.Body));
            }
        });

        [UnityTest]
        public IEnumerator CancelTimeoutAndRefusedAreDistinct() => UniTask.ToCoroutine(async () =>
        {
            using var cancel = new CancellationTokenSource();
            var request = new HttpRequest("GET", UriFor("/delay"));
            var pending = _network.SendAsync(request, cancel.Token);
            cancel.Cancel();
            Assert.IsTrue((await pending.SuppressCancellationThrow()).IsCanceled);
            request.Timeout = TimeSpan.FromMilliseconds(150);
            try { await _network.SendAsync(request, _deadline.Token); Assert.Fail("Expected timeout"); }
            catch (TimeoutException) { }
            using var unused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unused.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)unused.LocalEndPoint).Port;
            try
            {
                await _network.SendAsync(new HttpRequest("GET", new Uri("http://127.0.0.1:" + port)), _deadline.Token);
                Assert.Fail("Expected refused HTTP connection");
            }
            catch (IOException) { }
            try { await _network.ConnectAsync("127.0.0.1", port, _deadline.Token); Assert.Fail("Expected refused TCP connection"); }
            catch (SocketException) { }
        });

        [UnityTest]
        public IEnumerator SplitJoinedAndConcurrentMessagesPreserveOrder() => UniTask.ToCoroutine(async () =>
        {
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var received = new List<string>();
            var threads = new List<int>();
            int main = Thread.CurrentThread.ManagedThreadId;
            connection.MessageReceived += bytes =>
            {
                received.Add(Encoding.UTF8.GetString(bytes));
                threads.Add(Thread.CurrentThread.ManagedThreadId);
            };
            var payload = Encoding.UTF8.GetBytes("first");
            var first = connection.SendAsync(payload);
            payload[0] = 0;
            var second = connection.SendAsync(Encoding.UTF8.GetBytes("/frames"));
            var third = connection.SendAsync(Encoding.UTF8.GetBytes("last"));
            await UniTask.WhenAll(first, second, third);
            await UniTask.WaitUntil(() => received.Count >= 6, cancellationToken: _deadline.Token);
            CollectionAssert.AreEqual(new[] { "welcome", "first", "split", "joined", "", "last" }, received);
            Assert.IsTrue(threads.All(thread => thread == main));
            Assert.Throws<ArgumentOutOfRangeException>(() => { connection.SendAsync(new byte[1024 * 1024 + 1]); });
        });

        [UnityTest]
        public IEnumerator MalformedFramesEndOnlyTheirConnection() => UniTask.ToCoroutine(async () =>
        {
            foreach (string command in new[] { "/oversize", "/truncate", "/drop" })
            {
                var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
                var messages = new List<string>();
                var reasons = new List<DisconnectReason>();
                connection.MessageReceived += bytes => messages.Add(Encoding.UTF8.GetString(bytes));
                connection.Disconnected += reasons.Add;
                await connection.SendAsync(Encoding.UTF8.GetBytes(command));
                await UniTask.WaitUntil(() => reasons.Count > 0, cancellationToken: _deadline.Token);
                await connection.CloseAsync();
                await connection.CloseAsync();
                CollectionAssert.AreEqual(new[] { "welcome" }, messages);
                CollectionAssert.AreEqual(new[] { command == "/drop" ? DisconnectReason.RemoteClose : DisconnectReason.ProtocolError }, reasons);
            }
        });

        [UnityTest]
        public IEnumerator PeerCloseDuringWritesSettlesBoundedQueueOnce() => UniTask.ToCoroutine(async () =>
        {
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var reasons = new List<DisconnectReason>();
            connection.Disconnected += reasons.Add;
            await connection.SendAsync(Encoding.UTF8.GetBytes("/stall"));
            var sends = new List<UniTask<bool>>();
            var maximum = new byte[1024 * 1024];
            for (int i = 0; i < 16; i++) { sends.Add(Wrote(connection, maximum)); }
            Assert.Throws<InvalidOperationException>(() => { connection.SendAsync(maximum); });
            var results = await UniTask.WhenAll(sends).AttachExternalCancellation(_deadline.Token);
            Assert.Contains(false, results);
            await connection.CloseAsync();
            Assert.AreEqual(1, reasons.Count);
            Assert.AreNotEqual(DisconnectReason.LocalClose, reasons[0]);
        });

        private static async UniTask<bool> Wrote(IMessageConnection connection, byte[] bytes)
        {
            try { await connection.SendAsync(bytes); return true; }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
        }

        [UnityTest]
        public IEnumerator QueuedWriterObservesDisconnectBeforeItsInlineContinuation() => UniTask.ToCoroutine(async () =>
        {
            var session = new NetworkDemoSession(_network);
            await session.ConnectAsync(TcpPort, _deadline.Token);
            var connection = session.Connection;
            var reasonAtFailure = ObserveWriterFailure(session, connection.SendAsync(new byte[] { 1 }));
            await connection.CloseAsync();
            Assert.AreEqual(DisconnectReason.LocalClose, await reasonAtFailure);
        });

        private static async UniTask<DisconnectReason?> ObserveWriterFailure(NetworkDemoSession session, UniTask send)
        {
            try { await send; Assert.Fail("Queued send should fail on immediate close"); }
            catch (IOException) { return session.LastDisconnect; }
            return null;
        }

        [UnityTest]
        public IEnumerator RestartInsideDisconnectWaitsForRemainingListeners() => UniTask.ToCoroutine(async () =>
        {
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            UniTask restart = UniTask.CompletedTask;
            CoreState? secondListenerState = null;
            connection.Disconnected += _ => restart = _core.RestartAsync();
            connection.Disconnected += _ => secondListenerState = _core.State;
            var write = Wrote(connection, new byte[] { 1 });
            await connection.CloseAsync().SuppressCancellationThrow();
            Assert.IsFalse(await write);
            Assert.AreEqual(CoreState.Closing, secondListenerState);
            await restart.AttachExternalCancellation(_deadline.Token);
            Assert.AreEqual(CoreState.Running, _core.State);
        });

        [UnityTest]
        public IEnumerator RestartSettlesDependentSendAndRetiresOldWork() => UniTask.ToCoroutine(async () =>
        {
            var session = new NetworkDemoSession(_network);
            await session.ConnectAsync(TcpPort, _deadline.Token);
            var old = session.Connection;
            var reasons = new List<DisconnectReason>();
            old.Disconnected += reasons.Add;
            var request = _network.SendAsync(new HttpRequest("GET", UriFor("/delay")));
            _consumer.Pending = old.SendAsync(new byte[] { 5 });
            await _core.RestartAsync().AttachExternalCancellation(_deadline.Token);
            Assert.IsTrue(_consumer.SendFailedDuringShutdown);
            Assert.IsTrue((await request.SuppressCancellationThrow()).IsCanceled);
            CollectionAssert.AreEqual(new[] { DisconnectReason.ModuleShutdown }, reasons);
            Assert.Throws<ObjectDisposedException>(() => { old.SendAsync(new byte[0]); });
            await session.ConnectAsync(TcpPort, _deadline.Token);
            await UniTask.WaitUntil(() => session.LastMessage == "welcome", cancellationToken: _deadline.Token);
            await session.Connection.CloseAsync();
            Assert.AreEqual(DisconnectReason.LocalClose, session.LastDisconnect);
        });

        [UnityTest]
        public IEnumerator PlayRetirementCancelsWithoutAnotherFrame() => UniTask.ToCoroutine(async () =>
        {
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var reasons = new List<DisconnectReason>();
            connection.Disconnected += reasons.Add;
            var http = _network.SendAsync(new HttpRequest("GET", UriFor("/delay")));
            var connecting = _network.ConnectAsync("127.0.0.1", TcpPort);
            Core.ResetForPlay();
            Assert.AreEqual(UniTaskStatus.Canceled, http.Status);
            Assert.AreEqual(UniTaskStatus.Canceled, connecting.Status);
            Assert.IsTrue((await http.SuppressCancellationThrow()).IsCanceled);
            Assert.IsTrue((await connecting.SuppressCancellationThrow()).IsCanceled);
            CollectionAssert.AreEqual(new[] { DisconnectReason.ModuleShutdown }, reasons);
        });

        [UnityTest]
        public IEnumerator DemoUsesInterchangeableMessageConnection() => UniTask.ToCoroutine(async () =>
        {
            var peer = new TestPeer();
            var session = new NetworkDemoSession(peer);
            await session.ConnectAsync(1, _deadline.Token);
            await session.Connection.SendAsync(Encoding.UTF8.GetBytes("test transport"));
            Assert.AreEqual("test transport", session.LastMessage);
            await session.Connection.CloseAsync();
            Assert.IsNull(session.Connection);
        });

        private sealed class TestPeer : IMessageClient, IMessageConnection
        {
            public event Action<byte[]> MessageReceived;
            public event Action<DisconnectReason> Disconnected;
            public UniTask<IMessageConnection> ConnectAsync(string host, int port, CancellationToken token) =>
                UniTask.FromResult<IMessageConnection>(this);
            public UniTask SendAsync(byte[] message) { MessageReceived?.Invoke(message); return UniTask.CompletedTask; }
            public UniTask CloseAsync() { Disconnected?.Invoke(DisconnectReason.LocalClose); return UniTask.CompletedTask; }
        }

        private sealed class ShutdownConsumer : IModule, IAsyncShutdownModule
        {
            internal UniTask Pending;
            internal bool SendFailedDuringShutdown;
            public string GetModuleName() => "Network test consumer";
            public void OnRegister(ICore core) { }
            public UniTask<UniCoreException> Initialize(ICore core, CancellationToken token) => UniTask.FromResult<UniCoreException>(null);
            public void AfterInitialize(ICore core) { }
            public async UniTask<UniCoreException> Start(ICore core, CancellationToken token)
            {
                try { await core.GetModule<IMessageClient>().ConnectAsync("127.0.0.1", 1, token); Assert.Fail("Network admitted I/O during Core startup"); }
                catch (InvalidOperationException) { }
                return null;
            }
            public void AfterStart(ICore core) { }
            public async UniTask ShutdownAsync(ICore core, CancellationToken force)
            {
                try { await Pending; }
                catch (IOException) { SendFailedDuringShutdown = true; }
                finally { Pending = UniTask.CompletedTask; }
            }
            public void Shut(ICore core) { }
        }
    }
}
