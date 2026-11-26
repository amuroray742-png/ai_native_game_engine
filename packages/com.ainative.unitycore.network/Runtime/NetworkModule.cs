#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace AiNative.UnityCore.Network
{
    /// <summary>主线程拥有请求和连接。每次 Core 启动建立独立的运行期，关闭后旧连接失效。</summary>
    public sealed class NetworkModule : IModule, IAsyncShutdownModule, IHttpService, IMessageClient
    {
        private readonly int _maxMessageBytes;
        private readonly int _warnPendingSends;
        private readonly long _warnPendingSendBytes;
        private readonly ArrayPool<byte>? _bufferPool;
        private readonly bool _registerHttp;
        private readonly bool _registerTcp;
        private readonly HashSet<TcpMessageConnection> _connections = new HashSet<TcpMessageConnection>();
        private readonly List<Task> _retiring = new List<Task>();
        private readonly Dictionary<TcpMessageConnection, Task> _closing = new Dictionary<TcpMessageConnection, Task>();
        private Func<string, Task<IPAddress[]>> _resolveHost = Dns.GetHostAddressesAsync;
        private Action<Thread>? _startThread = null;
        private CancellationTokenSource? _lifetime;
        private CancellationTokenRegistration _stopConnections;
        private ICore? _core;

        private const int DefaultMessageBytes = 8 * 1024 * 1024;

        private const int DefaultPendingSends = 256;
        private const int DefaultPendingSendBytes = 4 * 1024 * 1024;

        public NetworkModule(int maxMessageBytes = DefaultMessageBytes) : this(true, true, maxMessageBytes) { }
        public NetworkModule(int maxMessageBytes, int maxPendingSends, long maxPendingSendBytes)
            : this(true, true, maxMessageBytes, maxPendingSends, maxPendingSendBytes) { }
        public static NetworkModule HttpOnly() => new NetworkModule(true, false, DefaultMessageBytes);
        public static NetworkModule TcpOnly(int maxMessageBytes = DefaultMessageBytes) => new NetworkModule(false, true, maxMessageBytes);
        public static NetworkModule TcpOnly(int maxMessageBytes, int maxPendingSends, long maxPendingSendBytes) =>
            new NetworkModule(false, true, maxMessageBytes, maxPendingSends, maxPendingSendBytes);

        private NetworkModule(bool registerHttp, bool registerTcp, int maxMessageBytes,
            int maxPendingSends = DefaultPendingSends, long maxPendingSendBytes = DefaultPendingSendBytes, ArrayPool<byte>? bufferPool = null)
        {
            if (maxMessageBytes <= 0 || maxMessageBytes > int.MaxValue - TcpFrames.HeaderSize)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            }
            if (maxPendingSends <= 0) { throw new ArgumentOutOfRangeException(nameof(maxPendingSends)); }
            if (maxPendingSendBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(maxPendingSendBytes)); }
            _bufferPool = bufferPool;
            _maxMessageBytes = maxMessageBytes;
            _warnPendingSends = maxPendingSends;
            _warnPendingSendBytes = maxPendingSendBytes;
            _registerHttp = registerHttp;
            _registerTcp = registerTcp;
        }

        public string GetModuleName() => "AiNative.UnityCore.Network";
        public void OnRegister(ICore core)
        {
            if (_registerHttp) { core.RegisterInterface<IHttpService, NetworkModule>(this); }
            if (_registerTcp) { core.RegisterInterface<IMessageClient, NetworkModule>(this); }
        }

        public UniTask<UniCoreException?> Initialize(ICore core, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UniTask.FromResult<UniCoreException?>(null);
        }

        public void AfterInitialize(ICore core) { }
        public UniTask<UniCoreException?> Start(ICore core, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _core = core;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Core stops its frame dispatcher before reverse-order async shutdown. A dependent
            // module can already be waiting on a queued send at that point.
            _stopConnections = _lifetime.Token.Register(StopConnections);
            return UniTask.FromResult<UniCoreException?>(null);
        }

        public void AfterStart(ICore core) { }

        private CancellationToken RunToken()
        {
            if (_core?.IsRunning != true || _lifetime == null || _lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("Network module is stopped.");
            }
            return _lifetime.Token;
        }

        public async UniTask<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(RunToken(), cancellationToken);
            operation.Token.ThrowIfCancellationRequested();
            if (request == null) { throw new ArgumentNullException(nameof(request)); }
            if (request.Uri == null || !request.Uri.IsAbsoluteUri ||
                (request.Uri.Scheme != Uri.UriSchemeHttps && request.Uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException("An absolute HTTP or HTTPS URI is required.", nameof(request));
            }
            var timeout = request.Timeout;
            if (timeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(request.Timeout)); }
            using var web = new UnityWebRequest(request.Uri, request.Method);
            web.downloadHandler = new DownloadHandlerBuffer();
            if (request.Body != null) { web.uploadHandler = new UploadHandlerRaw(request.Body); }
            if (request.Headers != null)
            {
                foreach (var header in request.Headers) { web.SetRequestHeader(header.Key, header.Value); }
            }
            var clock = Stopwatch.StartNew();
            var pending = web.SendWebRequest();
            try
            {
                await UniTask.WaitUntil(() => pending.isDone || clock.Elapsed >= timeout,
                    cancellationToken: operation.Token, cancelImmediately: true);
                operation.Token.ThrowIfCancellationRequested();
                if (!pending.isDone) { throw new TimeoutException("HTTP request timed out."); }
                if (web.result != UnityWebRequest.Result.Success && web.result != UnityWebRequest.Result.ProtocolError)
                {
                    throw new IOException(web.error);
                }
                return new HttpResponse(web.responseCode, web.GetResponseHeaders() ?? new Dictionary<string, string>(),
                    web.downloadHandler.data ?? Array.Empty<byte>());
            }
            finally
            {
                if (!pending.isDone) { web.Abort(); }
            }
        }

        public async UniTask<IMessageConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(RunToken(), cancellationToken);
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(host)) { throw new ArgumentException("TCP host is required.", nameof(host)); }
            if (port < 0 || port > 65535) { throw new ArgumentOutOfRangeException(nameof(port)); }
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal)) { addresses = new[] { literal }; }
            else
            {
                var lookup = _resolveHost(host);
                await WaitForLookupAsync(lookup, operation.Token);
                addresses = lookup.GetAwaiter().GetResult();
            }
            SocketException? lastError = null;
            foreach (var address in addresses)
            {
                Socket? socket = null;
                try
                {
                    socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    await WaitForNetworkTask(socket.ConnectAsync(new IPEndPoint(address, port)), operation.Token);
                    var connection = new TcpMessageConnection(socket, _maxMessageBytes, _warnPendingSends, _warnPendingSendBytes,
                        _core!.TimeMachine, RunToken(), OnRetired, OnReleased, _bufferPool, _startThread);
                    _connections.Add(connection);
                    socket = null; // The connection now owns it.
                    return connection;
                }
                catch (SocketException error) { lastError = error; }
                finally { socket?.Dispose(); }
            }
            throw lastError ?? new SocketException((int)SocketError.HostNotFound);
        }

        private static async UniTask WaitForLookupAsync(Task lookup, CancellationToken token)
        {
            try
            {
                await UniTask.WaitUntil(() => lookup.IsCompleted, cancellationToken: token, cancelImmediately: true);
                token.ThrowIfCancellationRequested();
                lookup.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // DNS owns no module resources. Observe late faults without delaying restart
                // or retaining a reference to this module or its next run.
                _ = TcpFrames.DrainAsync(lookup);
                throw;
            }
        }

        private async UniTask WaitForNetworkTask(Task task, CancellationToken token)
        {
            try
            {
                await UniTask.WaitUntil(() => task.IsCompleted, cancellationToken: token, cancelImmediately: true);
                token.ThrowIfCancellationRequested();
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TrackDrain(TcpFrames.DrainAsync(task));
                throw;
            }
        }

        private void OnRetired(TcpMessageConnection connection, Task drain)
        {
            _connections.Remove(connection);
            foreach (var closed in new List<TcpMessageConnection>(_closing.Keys))
            {
                if (_closing[closed].IsCompleted) { _closing.Remove(closed); }
            }
            _closing.Add(connection, drain);
            TrackDrain(drain);
        }

        private void OnReleased(TcpMessageConnection connection)
        {
            _closing.Remove(connection);
        }

        private void TrackDrain(Task drain)
        {
            _retiring.RemoveAll(task => task.IsCompleted);
            if (!drain.IsCompleted) { _retiring.Add(drain); }
        }

        private void StopConnections()
        {
            foreach (var connection in new List<TcpMessageConnection>(_connections))
            {
                connection.Stop(DisconnectReason.ModuleShutdown);
            }
        }

        public async UniTask ShutdownAsync(ICore core, CancellationToken forcedExit)
        {
            _lifetime?.Cancel();
            var drains = Task.WhenAll(_retiring);
            if (!drains.IsCompleted)
            {
                await UniTask.WaitUntil(() => drains.IsCompleted, cancellationToken: forcedExit, cancelImmediately: true);
            }
            drains.GetAwaiter().GetResult();
        }

        public void Shut(ICore core)
        {
            _lifetime?.Cancel();
            foreach (var connection in new List<TcpMessageConnection>(_closing.Keys)) { connection.ForceStop(); }
            _closing.Clear();
            _stopConnections.Dispose();
            _lifetime?.Dispose();
            _lifetime = null;
            _core = null;
            _retiring.Clear();
        }
    }
}
