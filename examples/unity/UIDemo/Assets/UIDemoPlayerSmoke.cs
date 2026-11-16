using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace AiNative.UIDemo
{
    // Opt-in acceptance driver. Normal interactive Players do not create this object.
    public sealed class UIDemoPlayerSmoke : MonoBehaviour
    {
        [Serializable]
        private sealed class Report
        {
            public bool success;
            public string backend, unity, graphics, error;
            public int frames, width, height;
            public List<string> checks = new List<string>();
        }

        private UIDemoBootstrap _host;
        private readonly Report _report = new Report();
        private string _result;
        private readonly List<string> _errors = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfRequested()
        {
            var args = Environment.GetCommandLineArgs();
            if (Application.isEditor || Array.IndexOf(args, "-uiSmoke") < 0) { return; }
            int index = Array.IndexOf(args, "-uiResult");
            if (index < 0 || index + 1 >= args.Length) { Debug.LogError("-uiResult path is required"); Application.Quit(1); return; }
            var driver = new GameObject("UIDemo Player acceptance").AddComponent<UIDemoPlayerSmoke>();
            DontDestroyOnLoad(driver.gameObject);
            driver._result = Path.GetFullPath(args[index + 1]);
            driver.RunAsync().Forget(Debug.LogException);
        }

        private void CaptureError(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) { _errors.Add(message); }
        }

        private async UniTask RunAsync()
        {
            Application.logMessageReceived += CaptureError;
            int firstFrame = Time.frameCount;
            try
            {
#if ENABLE_IL2CPP
                _report.backend = "Il2Cpp";
#else
                _report.backend = "Mono";
#endif
                _report.unity = Application.unityVersion;
                _report.graphics = SystemInfo.graphicsDeviceType.ToString();
                _host = FindFirstObjectByType<UIDemoBootstrap>();
                Require(_host != null, "Bootstrap scene host missing");
                await UniTask.WaitUntil(() => _host.Model != null && !_host.Busy || _host.LastError != null)
                    .Timeout(TimeSpan.FromSeconds(30), DelayType.Realtime);
                Require(_host.LastError == null && _host.Core.IsRunning, "Host startup failed");
                Require(_host.Assets.GetDiagnostics().Bundles.Count > 0, "Real bundles not loaded");
                var home = View<HomeView>();
                Require(home.PrefabImage == _host.Model.Image && View<HudView>().Picture.texture == _host.Model.Image,
                    "Prefab dependency and model lease must share the same image");
                Passed("bundle-shared-image");
                await Capture("home");

                await Click<HomeView>(DemoAction.Back);
                Require(_host.LastResult == "NoBackTarget", "Root Back rejection");
                await Click<HomeView>(DemoAction.Duplicate);
                Require(_host.LastResult == "AlreadyOpen", "Duplicate rejection");
                await Click<HomeView>(DemoAction.Next);
                await Click<GalleryView>(DemoAction.Third);
                await Click<DetailsView>(DemoAction.Transparent);
                Require(_host.UI.GetDiagnostics().PageDepth == 4 && View<DetailsView>().gameObject.activeInHierarchy, "Transparent stack");
                await Click<TransparentView>(DemoAction.Back);
                await Click<DetailsView>(DemoAction.Back);
                await Click<GalleryView>(DemoAction.Back);
                Require(_host.UI.GetDiagnostics().PageDepth == 1, "Page stack restoration");
                Passed("raycast-page-stack");

                await Click<HomeView>(DemoAction.Modal);
                Require(Hit(Command(home, DemoAction.Next)) != Command(home, DemoAction.Next).Button.gameObject, "Modal must block page");
                await Click<FirstModalView>(DemoAction.SecondModal);
                Require(_host.UI.GetDiagnostics().ModalCount == 2, "Nested modals");
                await Capture("modals");
                await Click<SecondModalView>(DemoAction.Back);
                await Click<FirstModalView>(DemoAction.Back);
                Require(!_host.UI.HasModal && _host.Events.currentSelectedGameObject == home.DefaultSelection.gameObject, "Focus restoration");
                Passed("modal-input-focus");

                _host.UI.TryGet<HudView>(out var hud, out var oldHud);
                await Click<HudView>(DemoAction.Hud);
                var reopened = await _host.UI.OpenAsync<HudView>();
                Require(reopened.Succeeded && reopened.View == hud && !reopened.Handle.Equals(oldHud), "Cache session renewal");
                Require((await _host.UI.CloseAsync(oldHud)).Error == UIError.InvalidHandle, "Stale handle accepted");
                var first = await _host.UI.OpenAsync<FirstModalView>();
                Require(first.Succeeded && (await _host.UI.CloseAsync(first.Handle)).Succeeded, "First cache close");
                var second = await _host.UI.OpenAsync<SecondModalView>();
                Require(second.Succeeded && (await _host.UI.CloseAsync(second.Handle)).Succeeded, "Second cache close");
                Require(first.View == null && _host.UI.GetDiagnostics().ClosedCacheCount == 1, "LRU eviction");
                Passed("cache-session-lru");

                await Click<HomeView>(DemoAction.CancelTransition);
                Require(_host.LastResult.Contains("Cancelled") && _host.UI.GetDiagnostics().PageDepth == 1 &&
                    !_host.UI.TryGet<GalleryView>(out _, out _) && _host.UI.GetDiagnostics().PendingOperations == 0, "Cancellation rollback");
                Passed("transition-cancellation");
                await Click<HudView>(DemoAction.Toast);
                View<ToastView>();
                Require(Hit(Command(home, DemoAction.Next)) == Command(home, DemoAction.Next).Button.gameObject, "Overlay blocks input");
                await UniTask.WaitUntil(() => !_host.UI.TryGet<ToastView>(out _, out _)).Timeout(TimeSpan.FromSeconds(5), DelayType.Realtime);
                Passed("toast-expiry-input");

                var previous = _host.Model;
                _host.UI.TryGet<HomeView>(out _, out var oldHome);
                previous.FailNextUnbind = true;
                bool failed = false;
                try { await _host.ReplaceSessionAsync(); } catch (UIOperationException) { failed = true; }
                Require(failed && ReferenceEquals(previous, _host.Model) && !previous.IsDisposed, "Failed Clear replaced model");
                Require((await _host.UI.PushAsync<HomeView>()).Error == UIError.NotRunning, "Failed Clear should stop opens");
                await _host.ReplaceSessionAsync();
                Require(previous.IsDisposed && !ReferenceEquals(previous, _host.Model) && _host.UI.GetDiagnostics().PageDepth == 1 &&
                    _host.UI.GetDiagnostics().ClosedCacheCount == 0 && (await _host.UI.ReleaseAsync(oldHome)).Error == UIError.InvalidHandle, "Clear retry/session isolation");
                Passed("clear-failure-retry");

                var core = _host.Core; var events = _host.Events; var root = GameObject.Find("UnityCore UI");
                await _host.ExecuteAsync(DemoAction.Scene);
                Require(SceneManager.GetActiveScene().name == "Other" && _host.Core == core && _host.Events == events &&
                    GameObject.Find("UnityCore UI") == root && core.IsRunning, "Scene persistence");
                Time.timeScale = 0;
                await Click<HomeView>(DemoAction.Next);
                Require(_host.UI.GetDiagnostics().PageDepth == 2, "Unscaled transitions");
                Time.timeScale = 1;
                Passed("scene-persistence-unscaled-transition");

                Require((await _host.UI.ClearAsync()).Succeeded, "Final Clear failed");
                var only = await _host.UI.OpenAsync<HudView>();
                Require(only.Succeeded, "Final cached owner open");
                var image = only.View.Picture.texture;
                _host.Model.ReleaseImage();
                Require((await _host.UI.CloseAsync(only.Handle)).Succeeded && image != null, "Cache dependency lifetime");
                Require((await _host.UI.TrimAsync()).Succeeded && image == null && _host.Assets.GetDiagnostics().Bundles.Count == 0,
                    "Trim did not release final bundle owner");
                Passed("trim-last-owner");
                await _host.ReplaceSessionAsync();
                await _host.CloseAsync();
                await UniTask.Yield();
                Require(_host.Model.IsDisposed && !core.IsRunning && _host.UI.GetDiagnostics().WindowCount == 0 &&
                    _host.UI.GetDiagnostics().PendingOperations == 0 && _host.UI.GetDiagnostics().FailedCleanupCount == 0 &&
                    _host.Assets.GetDiagnostics().Bundles.Count == 0 && _host.Assets.GetDiagnostics().Resources.Count == 0 &&
                    _host.Assets.GetDiagnostics().PendingOperations == 0, "Shutdown leaked resources");
                Passed("explicit-shutdown-zero-resources");
                Require(_errors.Count == 0, string.Join("\n", _errors));
                _report.success = true;
            }
            catch (Exception error) { _report.error = error.ToString(); }
            finally
            {
                Time.timeScale = 1;
                _report.frames = Time.frameCount - firstFrame;
                _report.width = Screen.width; _report.height = Screen.height;
                Application.logMessageReceived -= CaptureError;
                try { File.WriteAllText(_result, JsonUtility.ToJson(_report, true)); }
                finally { Application.Quit(_report.success ? 0 : 1); }
            }
        }

        private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
        private void Passed(string name) { _report.checks.Add(name); }
        private T View<T>() where T : DemoView
        { Require(_host.UI.TryGet<T>(out var view, out _), "Missing view " + typeof(T).Name); return view; }
        private static DemoCommand Command(DemoView view, DemoAction action) => view.Commands.Single(x => x.Action == action);
        private GameObject Hit(DemoCommand command)
        {
            Canvas.ForceUpdateCanvases();
            var rect = command.Button.GetComponent<RectTransform>();
            var pointer = new PointerEventData(_host.Events) { position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)) };
            var hits = new List<RaycastResult>(); _host.Events.RaycastAll(pointer, hits);
            Require(hits.Count > 0, "No raycast hit"); return hits[0].gameObject;
        }
        private async UniTask Click<T>(DemoAction action) where T : DemoView
        {
            var command = Command(View<T>(), action);
            Require(Hit(command) == command.Button.gameObject, "Raycast missed " + typeof(T).Name + "." + action);
            ExecuteEvents.Execute(command.Button.gameObject, new PointerEventData(_host.Events) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
            await UniTask.WaitUntil(() => !_host.Busy).Timeout(TimeSpan.FromSeconds(10), DelayType.Realtime);
            Require(_host.LastError == null, "Host action failed");
        }
        private async UniTask Capture(string name)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.3), ignoreTimeScale: true);
            // End-of-frame rendering is available in a graphical Player (no -batchmode).
            await UniTask.WaitForEndOfFrame(this);
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            try { File.WriteAllBytes(Path.ChangeExtension(_result, "." + name + ".png"), texture.EncodeToPNG()); }
            finally { Destroy(texture); }
        }
    }
}
