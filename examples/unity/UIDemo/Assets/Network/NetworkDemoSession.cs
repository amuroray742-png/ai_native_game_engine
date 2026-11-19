using System;
using System.Text;
using System.Threading;
using AiNative.UnityCore.Network;
using Cysharp.Threading.Tasks;

namespace AiNative.NetworkDemo
{
    // The same consumer runs with TCP, a test peer, or a future ordered message transport.
    public sealed class NetworkDemoSession
    {
        private readonly IMessageClient _client;
        private bool _connecting;
        public IMessageConnection Connection { get; private set; }
        public string LastMessage { get; private set; } = "";
        public DisconnectReason? LastDisconnect { get; private set; }

        public NetworkDemoSession(IMessageClient client) { _client = client; }

        public async UniTask ConnectAsync(int port, CancellationToken token)
        {
            if (_connecting || Connection != null) { throw new InvalidOperationException("Already connecting or connected."); }
            _connecting = true;
            IMessageConnection connection;
            try { connection = await _client.ConnectAsync("127.0.0.1", port, token); }
            finally { _connecting = false; }
            LastDisconnect = null;
            LastMessage = "";
            Connection = connection;
            connection.MessageReceived += bytes => LastMessage = Encoding.UTF8.GetString(bytes);
            connection.Disconnected += reason =>
            {
                LastDisconnect = reason;
                Connection = null;
            };
        }
    }
}
