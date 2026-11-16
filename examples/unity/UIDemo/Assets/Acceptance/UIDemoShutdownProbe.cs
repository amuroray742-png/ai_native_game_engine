using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UIDemo.Acceptance
{
    // Separate opt-in fixture. Setup uses host APIs; only a person/OS closes the Player.
    [DefaultExecutionOrder(32000)]
    public sealed class UIDemoShutdownProbe : MonoBehaviour
    {
        [Serializable]
        private sealed class Sample
        {
            public string kind, scenario, backend, unity, targetState, result, error, screenshot;
            public string[] assetErrors;
            public int frame, pages, windows, uiPending, failedCleanup, bundles, resources, assetPending, owners;
            public bool busy, running, modelDisposed, deliveryHeld, deliveryReleased;
            public float time, alpha;
        }

        private UIDemoBootstrap _host;
        private DelayedGalleryBackend _backend;
        private string _path, _scenario;
        private float _nextSample;
        private bool _captured, _quitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-uiShutdownScenario");
            if (Application.isEditor || index < 0) { return; }
            if (index + 2 >= args.Length || (args[index + 1] != "loading" && args[index + 1] != "exiting"))
            { throw new ArgumentException("-uiShutdownScenario requires loading|exiting and a new journal path"); }
            if (Array.IndexOf(args, "-uiSmoke") >= 0 || Array.IndexOf(args, "-uiNativeResult") >= 0)
            { throw new ArgumentException("Shutdown fixture cannot run with another acceptance driver"); }
            var host = FindFirstObjectByType<UIDemoBootstrap>();
            if (host == null) { throw new InvalidOperationException("UIDemo host missing"); }
            string path = Path.GetFullPath(args[index + 2]);
            using (new FileStream(path, FileMode.CreateNew)) { }
            host.AutoStart = false;
            var probe = host.gameObject.AddComponent<UIDemoShutdownProbe>();
            probe._host = host; probe._path = path; probe._scenario = args[index + 1];
            Application.logMessageReceived += probe.OnLog;
            Application.wantsToQuit += probe.OnWantsToQuit;
        }

        private void Start() { SetupAsync().Forget(Debug.LogException); }
        private async UniTask SetupAsync()
        {
            AssetsModule assets;
            if (_scenario == "loading")
            {
                string root = Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
                var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(root, "catalog.json")));
                catalog.Validate();
                _backend = new DelayedGalleryBackend(root);
                assets = new AssetsModule(() => (catalog, _backend));
            }
            else
            {
                _host.GalleryTransitionSeconds = 120;
                assets = new AssetsModule();
            }
            await _host.InitializeAsync(assets);
            Write(Snapshot("ready"));
            _host.Dispatch(DemoAction.Next);
            if (_scenario == "exiting")
            {
                await UniTask.WaitUntil(() => !_host.Busy);
                if (_host.LastError != null || _host.UI.GetDiagnostics().PageDepth != 2)
                { throw new InvalidOperationException("Gallery setup did not commit"); }
                Write(Snapshot("gallery-ready"));
                // Wait for the operator to start Back and then close the OS window.
                // A conversation handoff must not consume the exit-animation window.
            }
        }

        private bool IsTarget(Sample s) => s.running && s.busy && s.uiPending > 0 &&
            (_scenario == "loading" ? s.targetState == "Opening" && s.deliveryHeld && s.assetPending > 0
                                    : s.targetState == "Exiting" && s.alpha > 0 && s.alpha < 1);

        private void Update()
        {
            if (Time.unscaledTime < _nextSample) { return; }
            _nextSample = Time.unscaledTime + 0.25f;
            var sample = Snapshot("state");
            Write(sample);
            if (!_captured && IsTarget(sample))
            {
                _captured = true;
                CaptureAsync().Forget(Debug.LogException);
            }
        }

        private async UniTask CaptureAsync()
        {
            await UniTask.WaitForEndOfFrame(this);
            if (_quitting) { return; }
            var sample = Snapshot("screenshot");
            if (!IsTarget(sample)) { _captured = false; return; }
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                sample.screenshot = "pending-frame-" + sample.frame + ".png";
                using var stream = new FileStream(Path.Combine(Path.GetDirectoryName(_path), sample.screenshot), FileMode.CreateNew);
                var bytes = texture.EncodeToPNG();
                stream.Write(bytes, 0, bytes.Length);
                Write(sample);
            }
            finally { Destroy(texture); }
        }

        private Sample Snapshot(string kind)
        {
            var ui = _host.UI?.GetDiagnostics();
            var assets = _host.Assets?.GetDiagnostics();
            var target = ui?.Windows.FirstOrDefault(w => w.ViewType == typeof(GalleryView).FullName);
            var view = FindFirstObjectByType<GalleryView>();
            var visual = view != null ? view.Visual.GetComponent<CanvasGroup>() : null;
            return new Sample
            {
                kind = kind, scenario = _scenario,
#if ENABLE_IL2CPP
                backend = "Il2Cpp",
#else
                backend = "Mono",
#endif
                unity = Application.unityVersion, frame = Time.frameCount, time = Time.unscaledTime,
                targetState = target?.State.ToString() ?? "Absent", alpha = visual != null ? visual.alpha : -1,
                result = _host.LastResult, busy = _host.Busy, running = _host.Core != null && _host.Core.IsRunning,
                modelDisposed = _host.Model != null && _host.Model.IsDisposed,
                windows = ui?.WindowCount ?? -1, pages = ui?.PageDepth ?? -1, uiPending = ui?.PendingOperations ?? -1,
                failedCleanup = ui?.FailedCleanupCount ?? -1, bundles = assets?.Bundles.Count ?? -1,
                resources = assets?.Resources.Count ?? -1, assetPending = assets?.PendingOperations ?? -1,
                owners = assets?.Ownership?.Owners.Count ?? -1,
                assetErrors = assets?.Errors.ToArray() ?? Array.Empty<string>(),
                deliveryHeld = _backend != null && _backend.Held, deliveryReleased = _backend != null && _backend.Released
            };
        }

        private bool OnWantsToQuit() { Write(Snapshot("wants-to-quit")); return true; }
        private void OnApplicationQuit()
        {
            _quitting = true;
            Write(Snapshot("application-quit"));
        }
        private void OnDestroy()
        {
            if (_path == null) { return; }
            if (_backend != null)
            {
                // OnApplicationQuit ordering is not a retirement guarantee. Observe first.
                Write(Snapshot("retired"));
                if (_host.Core.IsRunning) { Debug.LogError("Core must retire before releasing delayed delivery"); }
                else
                {
                    _backend.ReleaseDelivery();
                    Write(Snapshot("late-delivery"));
                }
            }
            Write(Snapshot("destroy"));
            Application.wantsToQuit -= OnWantsToQuit;
            Application.logMessageReceived -= OnLog;
        }
        private void Write(Sample sample) { File.AppendAllText(_path, JsonUtility.ToJson(sample) + "\n"); }
        private void OnLog(string message, string trace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
            var sample = Snapshot("error"); sample.error = message + "\n" + trace; Write(sample);
        }
    }
}
