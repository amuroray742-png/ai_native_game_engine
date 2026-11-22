using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using AiNative.UnityCore;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using AiNative.UnityCore.Network;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Unity.Profiling;
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
        public IEnumerator ExternalHttpServiceCanReturnResponse() => UniTask.ToCoroutine(async () =>
        {
            IHttpService http = new StubHttp();
            var response = await http.SendAsync(new HttpRequest("GET", UriFor("/config")));
            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("stub", response.Headers["X-Source"]);
            CollectionAssert.AreEqual(new byte[] { 42 }, response.Body);
        });

        private sealed class StubHttp : IHttpService
        {
            public UniTask<HttpResponse> SendAsync(HttpRequest request, CancellationToken token = default) =>
                UniTask.FromResult(new HttpResponse(200, new Dictionary<string, string> { { "X-Source", "stub" } },
                    new byte[] { 42 }));
        }

        [UnityTest]
        public IEnumerator CoreCanRegisterHttpAndTcpIndependently() => UniTask.ToCoroutine(async () =>
        {
            await _core.CloseAsync();
            Core.ResetForPlay();
            _core = Core.Inst;
            var http = NetworkModule.HttpOnly();
            var tcp = NetworkModule.TcpOnly();
            _core.RegisterModule(http).RegisterModule(tcp);
            await _core.RunAsync();
            Assert.AreSame(http, _core.GetModule<IHttpService>());
            Assert.AreSame(tcp, _core.GetModule<IMessageClient>());
            Assert.AreEqual(200, (await http.SendAsync(new HttpRequest("GET", UriFor("/config")), _deadline.Token)).StatusCode);
            var connection = await tcp.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            await connection.CloseAsync();
        });

        [UnityTest]
        public IEnumerator IdleReplyStartsInsideReceiveCallback() => UniTask.ToCoroutine(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                peer.ReceiveTimeout = 2000;
                var stream = peer.GetStream();
                bool callbackRan = false;
                int received = 0;
                var reply = new byte[5];
                UniTask<int> completionThread = default;
                UniTaskStatus statusAtPeerRead = default;
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                connection.MessageReceived += _ =>
                {
                    var send = connection.SendAsync(new byte[] { 42 });
                    completionThread = ThreadAfterSend(send);
                    // Hold this Update until the peer sees the frame. A write deferred to the next Update fails.
                    try
                    {
                        while (received < reply.Length)
                        {
                            int count = stream.Read(reply, received, reply.Length - received);
                            if (count == 0) { break; }
                            received += count;
                        }
                    }
                    catch (IOException) { }
                    statusAtPeerRead = send.Status;
                    callbackRan = true;
                };
                stream.Write(new byte[4], 0, 4);
                peer.Client.Shutdown(SocketShutdown.Send);
                await UniTask.WaitUntil(() => callbackRan, cancellationToken: _deadline.Token);
                Assert.AreEqual(5, received);
                CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 42 }, reply);
                Assert.AreEqual(UniTaskStatus.Pending, statusAtPeerRead);
                Assert.AreEqual(mainThread, await completionThread);
                await connection.CloseAsync();
            }
            finally { listener.Stop(); }
        });

        private static async UniTask<int> ThreadAfterSend(UniTask send)
        {
            await send;
            return Thread.CurrentThread.ManagedThreadId;
        }

        private static T ConnectionField<T>(IMessageConnection connection, string name) =>
            (T)connection.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(connection);

        [UnityTest]
        public IEnumerator EarlierProtocolErrorSurvivesCloseBeforeNextUpdate() => UniTask.ToCoroutine(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                peer.GetStream().Write(new byte[] { 255, 255, 255, 255 }, 0, 4);
                var gate = ConnectionField<object>(connection, "_gate");
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                {
                    lock (gate) { return ConnectionField<DisconnectReason?>(connection, "_terminal").HasValue; }
                }, 2000));
                DisconnectReason? reason = null;
                connection.Disconnected += value => reason = value;
                var pending = connection.SendAsync(new byte[] { 1 });
                Assert.AreEqual(UniTaskStatus.Pending, pending.Status);
                await connection.CloseAsync();
                try { await pending; Assert.Fail("Send should fail after the protocol error."); }
                catch (InvalidDataException) { Assert.AreEqual(DisconnectReason.ProtocolError, reason); }
            }
            finally { listener.Stop(); }
        });

        [UnityTest]
        public IEnumerator ClosingOrRestartingJoinsBothDedicatedWorkers() => UniTask.ToCoroutine(async () =>
        {
            for (int cycle = 0; cycle < 4; cycle++)
            {
                var connection = await _network.ConnectAsync("localhost", TcpPort, _deadline.Token);
                var receiver = ConnectionField<Thread>(connection, "_receiver");
                var sender = ConnectionField<Thread>(connection, "_sender");
                Assert.IsTrue(receiver.IsAlive && sender.IsAlive && receiver.IsBackground && sender.IsBackground);
                Assert.AreNotEqual(sender.ManagedThreadId, receiver.ManagedThreadId);
                Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, sender.ManagedThreadId);
                Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, receiver.ManagedThreadId);
                bool welcomed = false;
                connection.MessageReceived += _ => welcomed = true;
                await UniTask.WaitUntil(() => welcomed, cancellationToken: _deadline.Token);
                if (cycle % 2 == 0) { await connection.CloseAsync().AttachExternalCancellation(_deadline.Token); }
                else { await _core.RestartAsync().AttachExternalCancellation(_deadline.Token); }
                Assert.IsFalse(sender.IsAlive || receiver.IsAlive, "A retired connection still owns a live worker.");
            }
        });

        [UnityTest]
        public IEnumerator SlowConsumerLeavesSecondFrameOnSocketAndReceivesBothBeforeEof() => UniTask.ToCoroutine(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                var received = new List<int>();
                DisconnectReason? reason = null;
                connection.MessageReceived += bytes => received.Add(bytes[0]);
                connection.Disconnected += value => reason = value;
                var socket = ConnectionField<Socket>(connection, "_socket");
                var gate = ConnectionField<object>(connection, "_gate");
                byte[] wire = { 0, 0, 0, 1, 7, 0, 0, 0, 1, 9 };
                peer.GetStream().Write(wire, 0, wire.Length);
                peer.Client.Shutdown(SocketShutdown.Send);
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                {
                    lock (gate)
                    {
                        return ConnectionField<byte[]>(connection, "_incoming") != null && socket.Available == 5;
                    }
                }, 2000), "The receiver must leave the second frame unread while the main thread holds the first.");
                await UniTask.WaitUntil(() => reason.HasValue, cancellationToken: _deadline.Token);
                CollectionAssert.AreEqual(new[] { 7, 9 }, received);
                Assert.AreEqual(DisconnectReason.RemoteClose, reason);
                await connection.CloseAsync();
            }
            finally { listener.Stop(); }
        });

        [UnityTest]
        public IEnumerator ShortWritesPreserveLargeFrames() => UniTask.ToCoroutine(async () =>
        {
            var payload = new byte[1024 * 1024];
            new Random(42).NextBytes(payload);
            // Force partial writes independent of kernel buffer size, then exercise the real wire path.
            var codec = typeof(NetworkModule).Assembly.GetType("AiNative.UnityCore.Network.TcpFrames");
            var write = (Action<byte[], Func<byte[], int, int, int>>)Delegate.CreateDelegate(
                typeof(Action<byte[], Func<byte[], int, int, int>>), codec.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static));
            using var captured = new MemoryStream();
            write(payload, (bytes, offset, count) =>
            {
                int accepted = Math.Min(4093, count);
                captured.Write(bytes, offset, accepted);
                return accepted;
            });
            CollectionAssert.AreEqual(payload, captured.ToArray());
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            byte[] reply = null;
            connection.MessageReceived += bytes => { if (bytes.Length == payload.Length) { reply = bytes; } };
            await connection.SendAsync(payload).AttachExternalCancellation(_deadline.Token);
            await UniTask.WaitUntil(() => reply != null, cancellationToken: _deadline.Token);
            CollectionAssert.AreEqual(payload, reply);
        });

        [UnityTest]
        public IEnumerator StableMessageListenersDispatchWithoutAllocation() => UniTask.ToCoroutine(async () =>
        {
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var dispatch = (Action<byte[]>)Delegate.CreateDelegate(typeof(Action<byte[]>), connection,
                connection.GetType().GetMethod("DispatchMessage", BindingFlags.NonPublic | BindingFlags.Instance));
            var payload = new byte[1];
            int first = 0, second = 0;
            Action<byte[]> listener = _ => second++;
            connection.MessageReceived += _ => first++;
            connection.MessageReceived += listener;
            dispatch(payload);
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 4096,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            GC.KeepAlive(new byte[128]);
            recorder.Stop();
            Assert.Greater(recorder.Count, 0, "The allocation recorder must detect the positive control.");
            recorder.Reset();
            recorder.Start();
            for (int i = 0; i < 256; i++) { dispatch(payload); }
            recorder.Stop();
            Assert.Zero(recorder.Count);
            connection.MessageReceived -= listener;
            dispatch(payload);
            Assert.AreEqual(258, first);
            Assert.AreEqual(257, second);
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
