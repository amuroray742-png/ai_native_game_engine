#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly HashSet<TcpMessageConnection> _connections = new HashSet<TcpMessageConnection>();
        private readonly List<Task> _retiring = new List<Task>();
        private CancellationTokenSource? _lifetime;
        private CancellationTokenRegistration _stopConnections;
        private ICore? _core;

        public NetworkModule(int maxMessageBytes = 1024 * 1024)
        {
            if (maxMessageBytes <= 0 || maxMessageBytes > int.MaxValue - 4)
            {
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            }
            _maxMessageBytes = maxMessageBytes;
        }

        public string GetModuleName() => "AiNative.UnityCore.Network";
        public void OnRegister(ICore core)
        {
            core.RegisterInterface<IHttpService, NetworkModule>(this);
            core.RegisterInterface<IMessageClient, NetworkModule>(this);
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
            var client = new TcpClient();
            Task? connecting = null;
            try
            {
                connecting = client.ConnectAsync(host, port);
                await UniTask.WaitUntil(() => connecting.IsCompleted, cancellationToken: operation.Token, cancelImmediately: true);
                operation.Token.ThrowIfCancellationRequested();
                connecting.GetAwaiter().GetResult();
                var connection = new TcpMessageConnection(client, _maxMessageBytes, _core!.TimeMachine, RunToken(), OnRetired);
                _connections.Add(connection);
                return connection;
            }
            catch
            {
                client.Dispose();
                if (connecting != null) { TrackDrain(TcpFrames.DrainAsync(connecting)); }
                throw;
            }
        }

        private void OnRetired(TcpMessageConnection connection, Task drain)
        {
            _connections.Remove(connection);
            TrackDrain(drain);
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
        }

        public void Shut(ICore core)
        {
            _lifetime?.Cancel();
            _stopConnections.Dispose();
            _lifetime?.Dispose();
            _lifetime = null;
            _core = null;
            _retiring.Clear();
        }
    }
}
