using System;
using System.Buffers;
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
        public IEnumerator BurstIsBufferedWithinCountAndDispatchedInOrder() => UniTask.ToCoroutine(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                var received = new List<int>();
                var frames = new List<int>();
                DisconnectReason? reason = null;
                connection.MessageReceived += bytes => { received.Add(bytes[0]); frames.Add(UnityEngine.Time.frameCount); };
                connection.Disconnected += value => reason = value;
                var socket = ConnectionField<Socket>(connection, "_socket");
                var wire = new byte[64 * 5];
                for (int i = 0; i < 64; i++)
                {
                    wire[i * 5 + 3] = 1;
                    wire[i * 5 + 4] = (byte)i;
                }
                peer.GetStream().Write(wire, 0, wire.Length);
                peer.Client.Shutdown(SocketShutdown.Send);
                WaitForBuffered(connection, 16);
                Assert.AreEqual(48 * 5, socket.Available);
                await UniTask.WaitUntil(() => reason.HasValue, cancellationToken: _deadline.Token);
                CollectionAssert.AreEqual(Enumerable.Range(0, 64), received);
                var batches = frames.GroupBy(frame => frame).Select(group => group.Count()).ToArray();
                Assert.AreEqual(16, batches[0]);
                Assert.IsTrue(batches.All(count => count <= 16));
                Assert.AreEqual(DisconnectReason.RemoteClose, reason);
            }
            finally { listener.Stop(); }
        });

        private static void WaitForBuffered(IMessageConnection connection, int count)
        {
            var gate = ConnectionField<object>(connection, "_gate");
            var incoming = ConnectionField<Queue<ArraySegment<byte>>>(connection, "_incoming");
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (gate) { return incoming.Count == count; }
            }, 3000));
        }

        [UnityTest]
        public IEnumerator ReceiveBudgetHoldsNextBodyOnSocket() => UniTask.ToCoroutine(async () =>
        {
            await UseTcpLimits(1024 * 1024, 256, 4 * 1024 * 1024);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                var first = new byte[4 + 1024 * 1024];
                first[1] = 16;
                var stream = peer.GetStream();
                peer.SendTimeout = 3000;
                stream.Write(first, 0, first.Length);
                // Freeze dispatch after the writes; observe the limit with both frames ready.
                stream.Write(new byte[] { 0, 0, 0, 1, 42 }, 0, 5);
                var socket = ConnectionField<Socket>(connection, "_socket");
                WaitForBuffered(connection, 1);
                Assert.IsTrue(SpinWait.SpinUntil(() => socket.Available == 1, 3000));
                var lengths = new List<int>();
                connection.MessageReceived += message => lengths.Add(message.Length);
                await UniTask.WaitUntil(() => lengths.Count == 2, cancellationToken: _deadline.Token);
                CollectionAssert.AreEqual(new[] { 1024 * 1024, 1 }, lengths);
                await connection.CloseAsync();
            }
            finally { listener.Stop(); }
        });

        [UnityTest]
        public IEnumerator CompletedWriteSurvivesCloseAndModuleShutdownBeforeUpdate() => UniTask.ToCoroutine(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                foreach (bool restart in new[] { false, true })
                {
                    var accepting = listener.AcceptTcpClientAsync();
                    var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                    using var peer = await accepting;
                    peer.ReceiveTimeout = 3000;
                    var send = connection.SendAsync(new byte[] { 42 });
                    var stream = peer.GetStream();
                    foreach (int expected in new[] { 0, 0, 0, 1, 42 }) { Assert.AreEqual(expected, stream.ReadByte()); }
                    Assert.AreEqual(UniTaskStatus.Pending, send.Status);
                    if (restart) { await _core.RestartAsync(); }
                    else { await connection.CloseAsync(); }
                    await send;
                }
            }
            finally { listener.Stop(); }
        });

        private async UniTask UseTcpLimits(int maxMessageBytes, int maxPendingSends, long maxPendingSendBytes, ArrayPool<byte> pool = null,
            bool includeConsumer = false)
        {
            await _core.CloseAsync();
            Core.ResetForPlay();
            _core = Core.Inst;
            _network = pool == null ? NetworkModule.TcpOnly(maxMessageBytes, maxPendingSends, maxPendingSendBytes) :
                (NetworkModule)Activator.CreateInstance(typeof(NetworkModule), BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new object[] { false, true, maxMessageBytes, maxPendingSends, maxPendingSendBytes, pool }, null);
            _core.RegisterModule(_network);
            if (includeConsumer) { _core.RegisterModule(_consumer); }
            await _core.RunAsync();
        }

        [UnityTest]
        public IEnumerator ThousandSmallSendsPreserveWireOrder() => UniTask.ToCoroutine(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                var sends = new UniTask[1024];
                var payload = new byte[2];
                for (int i = 0; i < sends.Length; i++)
                {
                    payload[0] = (byte)(i >> 8);
                    payload[1] = (byte)i;
                    sends[i] = connection.SendAsync(payload);
                }
                Assert.IsTrue(SpinWait.SpinUntil(() => ConnectionField<long>(connection, "_written") == sends.Length, 3000));
                Assert.IsTrue(sends.All(send => send.Status == UniTaskStatus.Pending));
                var wire = new byte[6 * sends.Length];
                await ReadReplyAsync(peer.GetStream(), wire, _deadline.Token);
                for (int i = 0; i < sends.Length; i++)
                {
                    CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 2, (byte)(i >> 8), (byte)i }, new ArraySegment<byte>(wire, i * 6, 6));
                }
                await UniTask.WhenAll(sends).AttachExternalCancellation(_deadline.Token);
                await connection.SendAsync(Array.Empty<byte>());
                await connection.CloseAsync();
            }
            finally { listener.Stop(); }
        });

        private static Action<long> BacklogSampler(IMessageConnection connection) =>
            (Action<long>)Delegate.CreateDelegate(typeof(Action<long>), connection, connection.GetType()
                .GetMethod("SampleBacklog", BindingFlags.Instance | BindingFlags.NonPublic));

        [UnityTest]
        public IEnumerator CompletedSendBacklogWarnsAfterOneSecondAndRearms() => UniTask.ToCoroutine(async () =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkModule(8, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => NetworkModule.TcpOnly(8, 1, 0));
            await UseTcpLimits(8, 1, 4);
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var sample = BacklogSampler(connection);
            long sec = System.Diagnostics.Stopwatch.Frequency;
            var logs = new List<string>();
            void Capture(string message, string stack, UnityEngine.LogType type)
            {
                if (type == UnityEngine.LogType.Warning && message.StartsWith("TCP send backlog")) { logs.Add(message); }
            }
            UnityEngine.Application.logMessageReceived += Capture;
            try
            {
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    var first = connection.SendAsync(new byte[8]);
                    var second = connection.SendAsync(Array.Empty<byte>());
                    Assert.IsTrue(SpinWait.SpinUntil(() => ConnectionField<long>(connection, "_written") == 2 * (cycle + 1), 3000));
                    long start = (cycle + 1) * 20 * sec;
                    sample(start);
                    sample(start + sec - 1);
                    Assert.AreEqual(2 * cycle, logs.Count);
                    sample(start + sec);
                    sample(start + 11 * sec - 1);
                    Assert.AreEqual(2 * cycle + 1, logs.Count);
                    StringAssert.Contains("pendingFrames=0 pendingBytes=0 writtenAwaitingDispatch=2", logs.Last());
                    sample(start + 11 * sec);
                    Assert.AreEqual(2 * cycle + 2, logs.Count);
                    await UniTask.WhenAll(first, second);
                    sample(start + 12 * sec);
                }
                await connection.CloseAsync();
            }
            finally { UnityEngine.Application.logMessageReceived -= Capture; }
        });

        [UnityTest]
        public IEnumerator StalledWriteWarnsAndAcceptsMoreFrames() => UniTask.ToCoroutine(async () =>
        {
            const int payloadSize = 8 * 1024 * 1024;
            var pool = new CountingPool { RoundSmallTo256 = true };
            await UseTcpLimits(payloadSize, 256, 1024, pool);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ReceiveBufferSize = 8192;
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                using var peer = await accepting;
                ConnectionField<Socket>(connection, "_socket").SendBufferSize = 8192;
                var writing = Wrote(connection, new byte[payloadSize]);
                Assert.IsTrue(SpinWait.SpinUntil(() => ConnectionField<int>(connection, "_writerState") == 1, 3000));
                var queued = Wrote(connection, Array.Empty<byte>());
                var sample = BacklogSampler(connection);
                sample(0);
                LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "TCP send backlog peer=.+ pendingFrames=2 pendingBytes=8388616 writtenAwaitingDispatch=0"));
                sample(System.Diagnostics.Stopwatch.Frequency);
                await connection.CloseAsync();
                Assert.IsFalse(await writing);
                Assert.IsFalse(await queued);
                Assert.AreEqual(2, pool.Rented);
                Assert.AreEqual(2, pool.Returned);
                Assert.Zero(pool.InvalidReturns);
            }
            finally { listener.Stop(); }
        });

        // Oversized, poisoned rentals make accidental exposure of capacity and early or
        // duplicate returns observable even when Unity's GC does not run.
        private sealed class CountingPool : ArrayPool<byte>
        {
            private readonly HashSet<byte[]> _live = new HashSet<byte[]>();
            public int Rented, Returned, InvalidReturns;
            public bool RoundSmallTo256;
            public override byte[] Rent(int size)
            {
                var buffer = new byte[RoundSmallTo256 && size <= 256 ? 256 : size + 17];
                lock (_live) { _live.Add(buffer); Rented++; }
                return buffer;
            }
            public override void Return(byte[] buffer, bool clearArray = false)
            {
                lock (_live)
                {
                    if (!_live.Remove(buffer)) { InvalidReturns++; }
                    Returned++;
                    for (int i = 0; i < buffer.Length; i++) { buffer[i] = 0xcc; }
                }
            }
        }

        [Test]
        public void SmallBufferRingBoundsRetentionAndReturnsToOwner()
        {
            var owner = new CountingPool { RoundSmallTo256 = true };
            var type = typeof(NetworkModule).Assembly.GetType("AiNative.UnityCore.Network.TcpSmallSendPool");
            var ring = (ArrayPool<byte>)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object[] { owner }, null);
            var buffers = new byte[130][];
            for (int i = 0; i < buffers.Length; i++) { buffers[i] = ring.Rent(256); }
            foreach (var buffer in buffers) { ring.Return(buffer); }
            Assert.AreEqual(2, owner.Returned);
            for (int i = 0; i < 1024; i++) { ring.Return(ring.Rent(4)); }
            Assert.AreEqual(130, owner.Rented);
            var larger = ring.Rent(257);
            Assert.GreaterOrEqual(larger.Length, 257);
            ring.Return(larger);
            type.GetMethod("Drain", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ring, null);
            Assert.AreEqual(owner.Rented, owner.Returned);
            Assert.Zero(owner.InvalidReturns);
        }

        [Test]
        public void ShortBatchWritesPublishOnlyWholeFramePrefixes()
        {
            var type = typeof(NetworkModule).Assembly.GetType("AiNative.UnityCore.Network.TcpWriteBatch");
            var batch = Activator.CreateInstance(type, true);
            var append = (Action<ArraySegment<byte>>)type.GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic)
                .CreateDelegate(typeof(Action<ArraySegment<byte>>), batch);
            var write = (Action<Func<byte[], int, int, int>, Action<int, int>>)type
                .GetMethod("Write", BindingFlags.Instance | BindingFlags.NonPublic)
                .CreateDelegate(typeof(Action<Func<byte[], int, int, int>, Action<int, int>>), batch);
            byte[] body = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (int i = 0; i < 32; i++) { append(new ArraySegment<byte>(body)); }
            foreach (int limit in new[] { 0, 255, 256, 257, 513, 8192 })
            {
                int offset = 0, frames = 0, bytes = 0;
                Func<byte[], int, int, int> send = (data, start, count) =>
                {
                    Assert.AreEqual(offset, start);
                    Assert.AreEqual(offset / 256, frames, "A later socket call ran before publishing the prefix");
                    if (start == limit && limit % 2 == 1) { throw new IOException("interrupted write"); }
                    int accepted = Math.Min(limit - start, Math.Min(count, 513));
                    for (int i = start; i < start + accepted; i++) { Assert.AreEqual((byte)i, data[i]); }
                    offset += accepted;
                    return accepted;
                };
                Action<int, int> publish = (n, b) => { frames += n; bytes += b; };
                if (limit == 8192) { write(send, publish); }
                else { Assert.Throws<IOException>(() => write(send, publish)); }
                Assert.AreEqual(limit / 256, frames);
                Assert.AreEqual(frames * 256, bytes);
            }
        }

        [UnityTest]
        public IEnumerator MixedBatchBoundariesPreserveWireAndReturnBuffersOnRetirement() => UniTask.ToCoroutine(async () =>
        {
            var owner = new CountingPool { RoundSmallTo256 = true };
            await UseTcpLimits(1024, 256, 65536, owner);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                foreach (bool restart in new[] { false, true })
                {
                    var accepting = listener.AcceptTcpClientAsync();
                    var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                    using var peer = await accepting;
                    var sends = new List<UniTask>();
                    using var expected = new MemoryStream();
                    var lengths = Enumerable.Repeat(252, 32).Concat(new[] { 253, 0, 252, 253 })
                        .Concat(Enumerable.Repeat(252, 33));
                    foreach (int length in lengths)
                    {
                        var body = Enumerable.Repeat((byte)sends.Count, length).ToArray();
                        expected.Write(new byte[] { 0, 0, 0, (byte)length }, 0, 4);
                        expected.Write(body, 0, body.Length);
                        sends.Add(connection.SendAsync(body));
                        Array.Clear(body, 0, body.Length);
                    }
                    var actual = new byte[(int)expected.Length];
                    await ReadReplyAsync(peer.GetStream(), actual, _deadline.Token);
                    Assert.IsTrue(expected.ToArray().SequenceEqual(actual));
                    if (restart) { await _core.RestartAsync(); }
                    else { await connection.CloseAsync(); }
                    await UniTask.WhenAll(sends);
                    Assert.AreEqual(owner.Rented, owner.Returned);
                    Assert.Zero(owner.InvalidReturns);
                }
            }
            finally { listener.Stop(); }
        });

        [UnityTest]
        public IEnumerator PooledFramesReturnOnDispatchFaultCloseRestartAndTruncation() => UniTask.ToCoroutine(async () =>
        {
            var pool = new CountingPool();
            await UseTcpLimits(64, 256, 1024, pool);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                foreach (string mode in new[] { "callback-close", "restart", "truncate" })
                {
                    var accept = listener.AcceptTcpClientAsync();
                    var conn = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                    using var peer = await accept;
                    var stream = peer.GetStream();
                    if (mode == "truncate")
                    {
                        stream.Write(new byte[] { 0, 0, 0, 3, 42 }, 0, 5);
                        peer.Client.Shutdown(SocketShutdown.Send);
                        DisconnectReason? reason = null;
                        conn.Disconnected += value => reason = value;
                        await UniTask.WaitUntil(() => reason.HasValue, cancellationToken: _deadline.Token);
                        Assert.AreEqual(DisconnectReason.ProtocolError, reason);
                    }
                    else
                    {
                        var send = conn.SendAsync(new byte[] { 42 });
                        var wire = new byte[5];
                        await ReadReplyAsync(stream, wire, _deadline.Token);
                        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 42 }, wire);
                        await send;
                        int observed = 0;
                        if (mode == "callback-close")
                        {
                            LogAssert.Expect(UnityEngine.LogType.Exception, "InvalidOperationException: expected TCP listener test failure");
                            conn.MessageReceived += bytes => throw new InvalidOperationException("expected TCP listener test failure");
                            conn.MessageReceived += bytes =>
                            {
                                conn.CloseAsync().Forget();
                                observed = bytes.Length == 1 ? bytes[0] : -1;
                            };
                        }
                        stream.Write(wire, 0, wire.Length);
                        stream.Write(wire, 0, wire.Length);
                        WaitForBuffered(conn, 2);
                        if (mode == "restart") { await _core.RestartAsync(); }
                        else
                        {
                            await UniTask.WaitUntil(() => observed != 0, cancellationToken: _deadline.Token);
                            Assert.AreEqual(42, observed);
                        }
                    }
                    await conn.CloseAsync();
                    Assert.AreEqual(pool.Rented, pool.Returned, mode);
                    Assert.Zero(pool.InvalidReturns, mode);
                }
                Assert.AreEqual(7, pool.Rented);
            }
            finally { listener.Stop(); }
        });

        [UnityTest]
        public IEnumerator HalfCloseFlushesLastCallbackReplyAndBoundsStalledPeer() => UniTask.ToCoroutine(async () =>
        {
            await UseTcpLimits(1024 * 1024, 16, 16 * (1024 * 1024 + 4));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ReceiveBufferSize = 8192;
            listener.Start();
            try
            {
                foreach (var (readReply, restart) in new[] { (true, false), (false, false), (false, true) })
                {
                    var accepting = listener.AcceptTcpClientAsync();
                    var connection = await _network.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, _deadline.Token);
                    using var peer = await accepting;
                    ConnectionField<Socket>(connection, "_socket").SendBufferSize = 8192;
                    var body = new byte[1024 * 1024];
                    body[body.Length - 1] = 42;
                    var sends = new List<UniTask<bool>>();
                    bool callbackReturned = false;
                    DisconnectReason? reason = null;
                    connection.MessageReceived += _ =>
                    {
                        // One frame can fit entirely in the Windows loopback buffers even
                        // with a small receive window. Fill the send queue for stalled peers.
                        int count = readReply ? 1 : 16;
                        for (int i = 0; i < count; i++) { sends.Add(Wrote(connection, body)); }
                        callbackReturned = true;
                    };
                    connection.Disconnected += value => reason = value;
                    var stream = peer.GetStream();
                    stream.Write(new byte[4], 0, 4);
                    peer.Client.Shutdown(SocketShutdown.Send);
                    await UniTask.WaitUntil(() => callbackReturned, cancellationToken: _deadline.Token);
                    if (readReply)
                    {
                        var received = new byte[body.Length + 4];
                        await ReadReplyAsync(stream, received, _deadline.Token);
                        CollectionAssert.AreEqual(new byte[] { 0, 16, 0, 0 }, received.Take(4));
                        CollectionAssert.AreEqual(body, received.Skip(4));
                    }
                    if (restart)
                    {
                        await UniTask.WaitUntil(() => ConnectionField<long?>(connection, "_replyDeadline").HasValue,
                            cancellationToken: _deadline.Token);
                        await _core.RestartAsync().AttachExternalCancellation(_deadline.Token);
                    }
                    await UniTask.WaitUntil(() => reason.HasValue, cancellationToken: _deadline.Token);
                    bool[] results = await UniTask.WhenAll(sends);
                    Assert.AreEqual(readReply, results[results.Length - 1]);
                    Assert.AreEqual(restart ? DisconnectReason.ModuleShutdown : DisconnectReason.RemoteClose, reason);
                }
            }
            finally { listener.Stop(); }
        });

        private static async System.Threading.Tasks.Task ReadReplyAsync(NetworkStream stream, byte[] received, CancellationToken token)
        {
            int offset = 0;
            while (offset < received.Length)
            {
                int count = await stream.ReadAsync(received, offset, received.Length - offset, token).ConfigureAwait(false);
                if (count == 0) { throw new EndOfStreamException("Peer closed before the complete reply."); }
                offset += count;
            }
        }

        [UnityTest]
        public IEnumerator ShortWritesPreserveLargeFrames() => UniTask.ToCoroutine(async () =>
        {
            var payload = new byte[8 * 1024 * 1024];
            new Random(42).NextBytes(payload);
            // Force partial writes independent of kernel buffer size, then exercise the real wire path.
            var codec = typeof(NetworkModule).Assembly.GetType("AiNative.UnityCore.Network.TcpFrames");
            var write = (Action<ArraySegment<byte>, Func<byte[], int, int, int>>)Delegate.CreateDelegate(
                typeof(Action<ArraySegment<byte>, Func<byte[], int, int, int>>), codec.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static));
            using var captured = new MemoryStream();
            write(new ArraySegment<byte>(payload), (bytes, offset, count) =>
            {
                int accepted = Math.Min(4093, count);
                captured.Write(bytes, offset, accepted);
                return accepted;
            });
            Assert.IsTrue(payload.SequenceEqual(captured.ToArray()));
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            byte[] reply = null;
            connection.MessageReceived += bytes => { if (bytes.Length == payload.Length) { reply = bytes.ToArray(); } };
            await connection.SendAsync(payload).AttachExternalCancellation(_deadline.Token);
            await UniTask.WaitUntil(() => reply != null, cancellationToken: _deadline.Token);
            Assert.IsTrue(payload.SequenceEqual(reply));
        });

        [UnityTest]
        public IEnumerator StableMessageListenersDispatchWithoutAllocation() => UniTask.ToCoroutine(async () =>
        {
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var dispatch = (MessageReceivedHandler)Delegate.CreateDelegate(typeof(MessageReceivedHandler), connection,
                connection.GetType().GetMethod("DispatchMessage", BindingFlags.NonPublic | BindingFlags.Instance));
            var payload = new byte[1];
            int first = 0, second = 0;
            MessageReceivedHandler listener = _ => second++;
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
            Assert.Throws<ArgumentOutOfRangeException>(() => { connection.SendAsync(new byte[8 * 1024 * 1024 + 1]); });
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
        public IEnumerator PeerCloseDuringWritesSettlesQueueOnce() => UniTask.ToCoroutine(async () =>
        {
            await UseTcpLimits(1024 * 1024, 16, 16 * (1024 * 1024 + 4));
            var connection = await _network.ConnectAsync("127.0.0.1", TcpPort, _deadline.Token);
            var reasons = new List<DisconnectReason>();
            connection.Disconnected += reasons.Add;
            await connection.SendAsync(Encoding.UTF8.GetBytes("/stall"));
            var sends = new List<UniTask<bool>>();
            var maximum = new byte[1024 * 1024];
            for (int i = 0; i < 16; i++) { sends.Add(Wrote(connection, maximum)); }
            sends.Add(Wrote(connection, maximum));
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
            var barrier = new WriterBarrierPool();
            await UseTcpLimits(1024, 256, 65536, barrier);
            var session = new NetworkDemoSession(_network);
            await session.ConnectAsync(TcpPort, _deadline.Token);
            var connection = session.Connection;
            var written = barrier.HoldAfterFirstWrite(connection);
            var reasonAtFailure = ObserveWriterFailure(session, connection.SendAsync(new byte[] { 1 }));
            await connection.CloseAsync();
            await written;
            Assert.IsFalse(barrier.TimedOut);
            Assert.AreEqual(DisconnectReason.LocalClose, await reasonAtFailure);
        });

        private sealed class WriterBarrierPool : ArrayPool<byte>
        {
            private IMessageConnection _connection;
            private byte[] _firstFrame;
            private volatile bool _holding;
            public bool TimedOut;

            internal UniTask HoldAfterFirstWrite(IMessageConnection connection)
            {
                _connection = connection;
                // Encoded length 257 takes the direct-write path. Return runs after PublishWrites.
                var result = connection.SendAsync(new byte[253]);
                Assert.IsTrue(SpinWait.SpinUntil(() => _holding, 3000));
                return result;
            }

            public override byte[] Rent(int size)
            {
                var buffer = Shared.Rent(size);
                if (size == 257) { _firstFrame = buffer; }
                return buffer;
            }

            public override void Return(byte[] buffer, bool clearArray = false)
            {
                if (ReferenceEquals(buffer, _firstFrame))
                {
                    _firstFrame = null;
                    _holding = true;
                    TimedOut = !SpinWait.SpinUntil(() => ConnectionField<int>(_connection, "_writerState") == 2, 5000);
                }
                Shared.Return(buffer, clearArray);
            }
        }

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
            await connection.CloseAsync().SuppressCancellationThrow();
            Assert.AreEqual(CoreState.Closing, secondListenerState);
            await restart.AttachExternalCancellation(_deadline.Token);
            Assert.AreEqual(CoreState.Running, _core.State);
        });

        [UnityTest]
        public IEnumerator RestartSettlesDependentSendAndRetiresOldWork() => UniTask.ToCoroutine(async () =>
        {
            var barrier = new WriterBarrierPool();
            await UseTcpLimits(1024, 256, 65536, barrier, includeConsumer: true);
            var session = new NetworkDemoSession(_network);
            await session.ConnectAsync(TcpPort, _deadline.Token);
            var old = session.Connection;
            var reasons = new List<DisconnectReason>();
            old.Disconnected += reasons.Add;
            var request = _network.SendAsync(new HttpRequest("GET", UriFor("/delay")));
            var written = barrier.HoldAfterFirstWrite(old);
            _consumer.Pending = old.SendAsync(new byte[] { 5 });
            var restart = _core.RestartAsync();
            await restart.AttachExternalCancellation(_deadline.Token);
            await written;
            Assert.IsFalse(barrier.TimedOut);
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
            public event MessageReceivedHandler MessageReceived;
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
