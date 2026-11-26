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
        public HttpResponse(long statusCode, IReadOnlyDictionary<string, string> headers, byte[] body)
        {
            StatusCode = statusCode;
            Headers = headers ?? throw new ArgumentNullException(nameof(headers));
            Body = body ?? throw new ArgumentNullException(nameof(body));
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

    /// <summary>正文仅在同步回调期间有效。跨回调保存时使用 ToArray()。</summary>
    public delegate void MessageReceivedHandler(ReadOnlySpan<byte> payload);

    public interface IMessageConnection
    {
        /// <summary>主线程按序交付借用的只读正文；连接成功后应立即订阅。</summary>
        event MessageReceivedHandler MessageReceived;
        event Action<DisconnectReason> Disconnected;
        /// <summary>复制并排队，成功表示整帧已写入本地流，不表示对端确认。正常关闭精确结算；强制退出保留已发布成功前缀，其余取消。持续积压会记录告警。</summary>
        UniTask SendAsync(byte[] message);
        /// <summary>立即停止收发，异步等待线程回收及发送结算，不在主线程等待线程退出。可重复调用；Core 关闭接管等待。重连须取得新连接。</summary>
        UniTask CloseAsync();
    }
}
