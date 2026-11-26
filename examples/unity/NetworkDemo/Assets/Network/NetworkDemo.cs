using System;
using System.Text;
using System.Threading;
using AiNative.UnityCore;
using AiNative.UnityCore.Network;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.NetworkDemo
{
    public sealed class NetworkDemo : MonoBehaviour
    {
        private string _httpPort = "18080";
        private string _tcpPort = "18081";
        private string _message = "Hello";
        private string _status = "";
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Core _core;
        private IHttpService _http;
        private NetworkDemoSession _session;
        private bool _busy;

        private async UniTask StartDemo()
        {
            var core = Core.Inst;
            core.RegisterModule(new NetworkModule());
            _core = core;
            await _core.RunAsync(_lifetime.Token);
            _http = _core.GetModule<IHttpService>();
            _session = new NetworkDemoSession(_core.GetModule<IMessageClient>());
            if (Environment.GetEnvironmentVariable("AINATIVE_NETWORK_SMOKE") == "1")
            {
                await NetworkSmoke.RunAsync(_http, _session, _lifetime.Token);
                await _core.CloseAsync();
#if ENABLE_IL2CPP
                Debug.Log("NETWORK_SMOKE_PASSED IL2CPP");
                Application.Quit(0);
#else
                throw new InvalidOperationException("IL2CPP Player required for smoke acceptance.");
#endif
            }
        }

        private void Start()
        {
            Application.runInBackground = true;
            StartDemo().Forget(error =>
            {
                _status = error.Message;
                Debug.LogException(error);
                if (Environment.GetEnvironmentVariable("AINATIVE_NETWORK_SMOKE") == "1") { Application.Quit(1); }
            });
        }

        private async UniTask Run(Func<UniTask> action)
        {
            _busy = true;
            try { await action(); }
            catch (Exception error) { _status = error.Message; }
            finally { _busy = false; }
        }

        private async UniTask Request()
        {
            var response = await _http.SendAsync(new HttpRequest("GET",
                new Uri("http://127.0.0.1:" + _httpPort + "/config")), _lifetime.Token);
            _status = "HTTP " + response.StatusCode + "   " + Encoding.UTF8.GetString(response.Body);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(24, 24, 450, 440), GUI.skin.box);
            GUILayout.Label("Loopback network client");
            GUILayout.Label("HTTP port");
            _httpPort = GUILayout.TextField(_httpPort);
            GUI.enabled = !_busy && _session != null;
            if (GUILayout.Button("GET /config")) { Run(Request).Forget(); }
            GUILayout.Label("TCP port");
            _tcpPort = GUILayout.TextField(_tcpPort);
            GUI.enabled = !_busy && _session != null && _session.Connection == null;
            if (GUILayout.Button("Connect")) { Run(() => _session.ConnectAsync(int.Parse(_tcpPort), _lifetime.Token)).Forget(); }
            GUI.enabled = !_busy && _session?.Connection != null;
            _message = GUILayout.TextField(_message);
            if (GUILayout.Button("Send")) { Run(() => _session.Connection.SendAsync(Encoding.UTF8.GetBytes(_message))).Forget(); }
            if (GUILayout.Button("Disconnect")) { Run(() => _session.Connection.CloseAsync()).Forget(); }
            GUI.enabled = true;
            GUILayout.Label(_status);
            GUILayout.Label("Received: " + _session?.LastMessage);
            GUILayout.Label("Disconnected: " + _session?.LastDisconnect);
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            if (_core != null) { _core.CloseAsync().Forget(Debug.LogException); }
        }
    }
}
