using System;
using System.IO;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UIDemo
{
    // Passive, opt-in observer. Never calls UI commands, CloseAsync or Application.Quit.
    [DefaultExecutionOrder(32000)]
    public sealed class UIDemoNativeProbe : MonoBehaviour
    {
        [Serializable]
        private sealed class Sample
        {
            public string kind, backend, unity, scene, selected, result, error, inputModule, screenshot;
            public int width, height;
            public int frame, session, pages, modals, cache, windows, uiPending, failedCleanup;
            public int bundles, resources, assetPending;
            public bool focused, busy, running, modelDisposed, mouseDown, submitDown, leftDown, rightDown, upDown, downDown;
            public float mouseX, mouseY;
        }

        private string _path, _lastState;
        private UIDemoBootstrap _host;
        private Core _core;
        private UIModule _ui;
        private AssetsModule _assets;
        private DemoModel _model;
        private float _nextSample;
        private bool _screenshots, _captureRequested, _quitting;
        private float _captureAfter;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-uiNativeResult");
            if (Application.isEditor || index < 0) { return; }
            if (index + 1 >= args.Length) { throw new ArgumentException("-uiNativeResult needs a new journal path"); }
            string path = Path.GetFullPath(args[index + 1]);
            using (new FileStream(path, FileMode.CreateNew)) { }
            var host = FindFirstObjectByType<UIDemoBootstrap>();
            if (host == null) { throw new InvalidOperationException("UIDemo host missing"); }
            var probe = host.gameObject.AddComponent<UIDemoNativeProbe>();
            probe._path = path;
            probe._host = host;
            probe._screenshots = Array.IndexOf(args, "-uiNativeScreenshots") >= 0;
            Application.logMessageReceived += probe.OnLog;
        }

        private void Update()
        {
            _core = _host.Core; _ui = _host.UI; _assets = _host.Assets; _model = _host.Model;
            bool mouse = Input.GetMouseButtonDown(0), submit = Input.GetKeyDown(KeyCode.Return);
            bool left = Input.GetKeyDown(KeyCode.LeftArrow), right = Input.GetKeyDown(KeyCode.RightArrow);
            bool up = Input.GetKeyDown(KeyCode.UpArrow), down = Input.GetKeyDown(KeyCode.DownArrow);
            if (mouse || submit || left || right || up || down)
            {
                var input = Snapshot("input");
                input.mouseDown = mouse; input.submitDown = submit;
                input.leftDown = left; input.rightDown = right; input.upDown = up; input.downDown = down;
                input.mouseX = Input.mousePosition.x; input.mouseY = Input.mousePosition.y;
                Write(input);
            }
            if (Time.unscaledTime < _nextSample) { return; }
            _nextSample = Time.unscaledTime + 0.1f;
            var sample = Snapshot("state");
            int frame = sample.frame;
            sample.frame = 0;
            string state = JsonUtility.ToJson(sample);
            sample.frame = frame;
            if (state != _lastState)
            {
                _lastState = state; Write(sample);
                _captureRequested = _screenshots;
                _captureAfter = Time.unscaledTime + 0.35f;
            }
            if (_captureRequested && Time.unscaledTime >= _captureAfter && sample.running && !sample.busy && sample.uiPending == 0)
            {
                _captureRequested = false;
                CaptureAsync().Forget(Debug.LogException);
            }
        }

        private async UniTask CaptureAsync()
        {
            // Capture the actual graphical Player after native input and rendering.
            // This is the client viewport, not the OS title bar or a Camera RenderTexture.
            await UniTask.WaitForEndOfFrame(this);
            if (_quitting) { return; }
            var sample = Snapshot("screenshot");
            if (!sample.running || sample.busy || sample.uiPending != 0) { return; }
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                sample.width = texture.width; sample.height = texture.height;
                sample.screenshot = "native-frame-" + sample.frame + ".png";
                string path = Path.Combine(Path.GetDirectoryName(_path), sample.screenshot);
                using (var file = new FileStream(path, FileMode.CreateNew))
                {
                    byte[] bytes = texture.EncodeToPNG();
                    file.Write(bytes, 0, bytes.Length);
                }
                Write(sample);
            }
            finally { Destroy(texture); }
        }

        private Sample Snapshot(string kind)
        {
            var ui = _ui?.GetDiagnostics();
            var assets = _assets?.GetDiagnostics();
            var selected = _host != null && _host.Events != null ? _host.Events.currentSelectedGameObject : null;
            return new Sample
            {
                kind = kind,
#if ENABLE_IL2CPP
                backend = "Il2Cpp",
#else
                backend = "Mono",
#endif
                unity = Application.unityVersion, frame = Time.frameCount, focused = Application.isFocused,
                scene = SceneManager.GetActiveScene().name, selected = selected == null ? "" : selected.name,
                result = _host != null ? _host.LastResult : "", busy = _host != null && _host.Busy,
                inputModule = _host != null && _host.Events != null && _host.Events.currentInputModule != null
                    ? _host.Events.currentInputModule.GetType().Name : "",
                session = _model?.Session ?? 0, modelDisposed = _model != null && _model.IsDisposed,
                running = _core != null && _core.IsRunning,
                pages = ui?.PageDepth ?? -1, modals = ui?.ModalCount ?? -1, cache = ui?.ClosedCacheCount ?? -1,
                windows = ui?.WindowCount ?? -1, uiPending = ui?.PendingOperations ?? -1, failedCleanup = ui?.FailedCleanupCount ?? -1,
                bundles = assets?.Bundles.Count ?? -1, resources = assets?.Resources.Count ?? -1, assetPending = assets?.PendingOperations ?? -1
            };
        }

        private void Write(Sample sample) { File.AppendAllText(_path, JsonUtility.ToJson(sample) + "\n"); }
        private void OnLog(string message, string trace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
            Write(new Sample { kind = "error", error = message + "\n" + trace });
        }
        private void OnApplicationQuit() { _quitting = true; Write(Snapshot("application-quit")); }
        private void OnDestroy()
        {
            if (_path == null) { return; }
            Write(Snapshot("destroy"));
            Application.logMessageReceived -= OnLog;
        }
    }
}
