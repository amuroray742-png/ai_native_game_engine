#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.Network
{
    /// <summary>主线程发起和取消。HTTP 状态（含 4xx/5xx）随响应交付，网络故障、超时和取消使任务失败。</summary>
    public interface IHttpService
    {
        UniTask<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class HttpRequest
    {
        public HttpRequest(string method, Uri uri)
        {
            Method = method;
            Uri = uri;
        }

        public string Method { get; }
        public Uri Uri { get; }
        public IReadOnlyDictionary<string, string>? Headers { get; set; }
        public byte[]? Body { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class HttpResponse
    {
        internal HttpResponse(long statusCode, IReadOnlyDictionary<string, string> headers, byte[] body)
        {
            StatusCode = statusCode;
            Headers = headers;
            Body = body;
        }

        public long StatusCode { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public byte[] Body { get; }
    }

    /// <summary>显式创建新连接。调用及完成在 Unity 主线程。消息传输实现由装配方选择。</summary>
    public interface IMessageClient
    {
        UniTask<IMessageConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
    }

    public enum DisconnectReason { LocalClose, RemoteClose, TransportError, ProtocolError, ModuleShutdown }

    public interface IMessageConnection
    {
        /// <summary>主线程按序交付独立的消息数组；连接成功后应立即订阅。</summary>
        event Action<byte[]> MessageReceived;
        event Action<DisconnectReason> Disconnected;
        /// <summary>复制并排队，完成表示已写入本地流。队列已满时报告失败，调用方可等待先前的发送。</summary>
        UniTask SendAsync(byte[] message);
        /// <summary>立即停止收发，等待套接字操作收尾，可重复调用。重连须取得新连接。</summary>
        UniTask CloseAsync();
    }
}
