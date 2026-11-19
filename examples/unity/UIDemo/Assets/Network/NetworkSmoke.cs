using System;
using System.Text;
using System.Threading;
using AiNative.UnityCore.Network;
using Cysharp.Threading.Tasks;

namespace AiNative.NetworkDemo
{
    internal static class NetworkSmoke
    {
        internal static int Port(string protocol) =>
            int.Parse(Environment.GetEnvironmentVariable("AINATIVE_NETWORK_" + protocol) ??
                throw new InvalidOperationException("Start through scripts/verify_unity_network.py to supply loopback ports."));

        internal static async UniTask RunAsync(IHttpService http, NetworkDemoSession session, CancellationToken token)
        {
            var reply = await http.SendAsync(new HttpRequest("GET", new Uri("http://127.0.0.1:" + Port("HTTP") + "/config")), token);
            if (reply.StatusCode != 200 || Encoding.UTF8.GetString(reply.Body) != "network-demo")
            {
                throw new InvalidOperationException("Unexpected configuration response.");
            }
            int port = Port("TCP");
            await session.ConnectAsync(port, token);
            await UniTask.WaitUntil(() => session.LastMessage == "welcome", cancellationToken: token);
            var first = session.Connection;
            await first.SendAsync(Encoding.UTF8.GetBytes("echo"));
            await UniTask.WaitUntil(() => session.LastMessage == "echo", cancellationToken: token);
            await first.SendAsync(Encoding.UTF8.GetBytes("/drop"));
            await UniTask.WaitUntil(() => session.LastDisconnect == DisconnectReason.RemoteClose, cancellationToken: token);
            await session.ConnectAsync(port, token);
            if (ReferenceEquals(first, session.Connection)) { throw new InvalidOperationException("Reused closed connection."); }
            await UniTask.WaitUntil(() => session.LastMessage == "welcome", cancellationToken: token);
            await session.Connection.SendAsync(Encoding.UTF8.GetBytes("reconnected"));
            await UniTask.WaitUntil(() => session.LastMessage == "reconnected", cancellationToken: token);
            await session.Connection.CloseAsync();
            if (session.LastDisconnect != DisconnectReason.LocalClose) { throw new InvalidOperationException("Expected local close."); }
        }
    }
}
