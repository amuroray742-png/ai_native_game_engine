using System;
using System.Collections;
using System.Threading;
using AiNative.UnityCore;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Network.Tests
{
    public sealed class NetworkPlayCycleTests
    {
        private const string Evidence = "AiNative.Network.Tests.PlayExit.";
        private static bool ReloadEnabled => !EditorSettings.enterPlayModeOptionsEnabled ||
            !EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload);

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) { yield return new ExitPlayMode(); }
            SessionState.EraseInt(Evidence + "tcp");
            SessionState.EraseInt(Evidence + "http");
        }

        [UnityTest]
        public IEnumerator RealPlayExitsCancelHttpAndDisconnectBeforeReentry()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetInt(Evidence + "tcp", 0);
            SessionState.SetInt(Evidence + "http", 0);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled);
            yield return BeginRequests().ToCoroutine();
            yield return new ExitPlayMode();
            AssertExitCount(1);

            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled);
            yield return BeginRequests().ToCoroutine();
            yield return new ExitPlayMode();
            AssertExitCount(2);
        }

        private static void AssertExitCount(int expected)
        {
            Assert.AreEqual(expected, SessionState.GetInt(Evidence + "tcp", -1));
            Assert.AreEqual(expected, SessionState.GetInt(Evidence + "http", -1));
        }

        private static async UniTask BeginRequests()
        {
            var core = Core.Inst;
            Assert.AreEqual(CoreState.Stopped, core.State);
            var network = new NetworkModule();
            core.RegisterModule(network);
            await core.RunAsync();
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfterSlim(TimeSpan.FromSeconds(10), DelayType.Realtime);
            int port = int.Parse(Environment.GetEnvironmentVariable("AINATIVE_NETWORK_TCP") ?? "18081");
            var connection = await network.ConnectAsync("127.0.0.1", port, timeout.Token);
            bool ready = false;
            connection.MessageReceived += _ => ready = true;
            connection.Disconnected += reason =>
            {
                if (reason == DisconnectReason.ModuleShutdown) { Count("tcp"); }
            };
            await UniTask.WaitUntil(() => ready, cancellationToken: timeout.Token);
            var httpPort = Environment.GetEnvironmentVariable("AINATIVE_NETWORK_HTTP") ?? "18080";
            ObserveCancellation(network.SendAsync(new HttpRequest("GET", new Uri("http://127.0.0.1:" + httpPort + "/hold")))).Forget();
        }

        private static async UniTask ObserveCancellation(UniTask<HttpResponse> pending)
        {
            var result = await pending.SuppressCancellationThrow();
            if (result.IsCanceled) { Count("http"); }
        }

        private static void Count(string key)
        {
            SessionState.SetInt(Evidence + key, SessionState.GetInt(Evidence + key, 0) + 1);
        }
    }
}
