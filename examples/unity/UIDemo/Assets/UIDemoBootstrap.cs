using System;
using System.Threading;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AiNative.UIDemo
{
    public sealed class UIDemoBootstrap : MonoBehaviour
    {
        public const string Content = "Assets/Content/";
        public EventSystem Events;
        public Text Status;
        public Text Diagnostics;
        public DemoCommand[] Commands;
        public bool AutoStart = true;
        // Configure before InitializeAsync; acceptance can make Gallery transitions observable.
        internal float GalleryTransitionSeconds { get; set; } = 0.35f;
        internal UIOptions DisplayOptions { get; set; } = new UIOptions(closedCacheCapacity: 1);
        public Core Core { get; private set; }
        public AssetsModule Assets { get; private set; }
        public UIModule UI { get; private set; }
        public DemoModel Model { get; private set; }
        public bool Busy { get; private set; }
        public string LastResult { get; private set; } = "Build content, then enter Play.";
        public Exception LastError { get; private set; }
        private int _session;
        private bool _closing;
        private float _toastUntil;
        private UIHandle _toastHandle;
        private DemoModel _toastSession;
        private const double DiagnosticsIntervalSeconds = 0.25;
        private double _nextDiagnosticsAt;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private void Awake() { DontDestroyOnLoad(gameObject); }
        private void Start()
        {
            Application.runInBackground = true;
            foreach (var command in Commands)
            {
                var action = command.Action;
                command.Button.onClick.AddListener(() => Dispatch(action));
            }
            if (AutoStart) { StartObservedAsync().Forget(Debug.LogException); }
        }

        private async UniTask StartObservedAsync()
        {
            try { await InitializeAsync(new AssetsModule()); }
            catch (Exception error) { ReportError(error); }
        }

        public async UniTask InitializeAsync(AssetsModule assets)
        {
            Busy = true;
            try
            {
                Core = Core.Inst;
                Assets = assets;
                UI = new UIModule(DisplayOptions, Events);
                RegisterWindows();
                Core.RegisterModule(Assets).RegisterModule(UI);
                await Core.RunAsync(_lifetime.Token);
                await CreateSessionAsync();
            }
            finally { Busy = false; }
        }

        private void RegisterWindows()
        {
            Register<HomeView>(UIRole.Page, true);
            Register<GalleryView>(UIRole.Page, true);
            Register<DetailsView>(UIRole.Page, true);
            Register<TransparentView>(UIRole.Page, false);
            Register<HudView>(UIRole.Hud);
            Register<FirstModalView>(UIRole.Modal);
            Register<SecondModalView>(UIRole.Modal);
            Register<ToastView>(UIRole.Overlay);
        }

        private void Register<T>(UIRole role, bool opaque = false) where T : DemoView
        {
            UI.Register(new UIWindowDefinition<T>(Content + typeof(T).Name, role,
                (view, scope) => new DemoPresenter(view, Model, Dispatch), UICachePolicy.Cache,
                opaqueFullscreen: opaque, allowHudInput: role == UIRole.Page,
                transition: new UITransitionOptions(typeof(T) == typeof(GalleryView) ? GalleryTransitionSeconds : 0.35f,
                    true, new Vector2(110, 0), 0.96f)));
        }

        private async UniTask CreateSessionAsync()
        {
            var image = await Assets.LoadAsync<Texture2D>(Content + "SharedImage", _lifetime.Token);
            Model = new DemoModel(++_session, image);
            var home = await UI.PushAsync<HomeView>(_lifetime.Token);
            SetResult(home.Error);
            if (!home.Succeeded) { return; }
            var hud = await UI.OpenAsync<HudView>(_lifetime.Token);
            SetResult(hud.Error);
        }

        public void Dispatch(DemoAction action) { ObserveActionAsync(action).Forget(Debug.LogException); }
        private async UniTask ObserveActionAsync(DemoAction action)
        {
            try { await ExecuteAsync(action); }
            catch (OperationCanceledException) { LastResult = "Cancelled; previous page retained."; }
            catch (UIOperationException error) when ((_closing || (Core != null && !Core.IsRunning)) && IsCancelledClose(error))
            {
                // Back/Hud exit can be interrupted by host shutdown just like toast exit.
                // Do not hide committed operations, cleanup ownership, or mixed failures.
                LastResult = "Cancelled during host shutdown.";
            }
            catch (Exception error) { ReportError(error); }
        }

        public async UniTask ExecuteAsync(DemoAction action)
        {
            if (Busy || _closing) { LastResult = "Host operation in progress."; return; }
            Busy = true;
            LastError = null;
            try
            {
                switch (action)
                {
                    case DemoAction.Next: SetResult((await UI.PushAsync<GalleryView>(_lifetime.Token)).Error); break;
                    case DemoAction.Third: SetResult((await UI.PushAsync<DetailsView>(_lifetime.Token)).Error); break;
                    case DemoAction.Transparent: SetResult((await UI.PushAsync<TransparentView>(_lifetime.Token)).Error); break;
                    case DemoAction.Modal: SetResult((await UI.OpenAsync<FirstModalView>(_lifetime.Token)).Error); break;
                    case DemoAction.SecondModal: SetResult((await UI.OpenAsync<SecondModalView>(_lifetime.Token)).Error); break;
                    case DemoAction.Toast:
                        var toast = await UI.OpenAsync<ToastView>(_lifetime.Token);
                        SetResult(toast.Error);
                        if (toast.Succeeded)
                        {
                            _toastHandle = toast.Handle; _toastSession = Model;
                            _toastUntil = Time.unscaledTime + 2;
                        }
                        break;
                    case DemoAction.Back: SetResult((await UI.BackAsync()).Error); break;
                    case DemoAction.Hud:
                        if (UI.TryGet<HudView>(out _, out var hud)) { SetResult((await UI.CloseAsync(hud)).Error); }
                        else { SetResult((await UI.OpenAsync<HudView>(_lifetime.Token)).Error); }
                        break;
                    case DemoAction.Duplicate: SetResult((await UI.PushAsync<HomeView>(_lifetime.Token)).Error); break;
                    case DemoAction.Trim: SetResult((await UI.TrimAsync()).Error); break;
                    case DemoAction.FaultClear: Model.FailNextUnbind = true; await ReplaceSessionAsync(); break;
                    case DemoAction.Clear: await ReplaceSessionAsync(); break;
                    case DemoAction.Scene: await SceneManager.LoadSceneAsync("Other", LoadSceneMode.Single); LastResult = "Single scene changed; UI and session retained."; break;
                    case DemoAction.CancelTransition:
                        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                        {
                            cancel.CancelAfterSlim(TimeSpan.FromSeconds(0.12));
                            SetResult((await UI.PushAsync<GalleryView>(cancel.Token)).Error);
                        }
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(action));
                }
            }
            finally { Busy = false; }
        }

        public async UniTask ReplaceSessionAsync()
        {
            var result = await UI.ClearAsync();
            SetResult(result.Error);
            if (!result.Succeeded) { return; }
            Model.Dispose();
            await CreateSessionAsync();
        }

        public async UniTask CloseAsync()
        {
            _closing = true;
            _lifetime.Cancel();
            if (Core != null) { await Core.CloseAsync(); }
            Model?.Dispose();
        }

        private void SetResult(UIError error) { LastResult = error == UIError.None ? "Ready" : error.ToString(); }
        private void ReportError(Exception error) { LastError = error; LastResult = error.Message; Debug.LogException(error); }
        private void Update()
        {
            Status.text = LastResult;
            if (UI == null) { return; }
            if (_toastSession != null && !Busy && !_closing && Time.unscaledTime >= _toastUntil)
            {
                bool current = ReferenceEquals(_toastSession, Model);
                _toastSession = null;
                if (current) { CloseToastAsync(_toastHandle).Forget(Debug.LogException); }
            }
            // Full UI/Assets diagnostics traverse components and allocate snapshots.
            // Keep status and toast processing per-frame, but sample the panel at 4 Hz.
            double now = Time.unscaledTimeAsDouble;
            if (now < _nextDiagnosticsAt) { return; }
            _nextDiagnosticsAt = now + DiagnosticsIntervalSeconds;
            var ui = UI.GetDiagnostics();
            var assets = Assets.GetDiagnostics();
            Diagnostics.text = $"SESSION {_session}   |   pages {ui.PageDepth} / modals {ui.ModalCount} / cache {ui.ClosedCacheCount} / pending {ui.PendingOperations} / failed {ui.FailedCleanupCount}   |   bundles {assets.Bundles.Count}";
        }

        private async UniTask CloseToastAsync(UIHandle handle)
        {
            try
            {
                var result = await UI.CloseAsync(handle);
                if (!result.Succeeded && result.Error != UIError.InvalidHandle) { SetResult(result.Error); }
            }
            catch (UIOperationException error) when (IsCancelledClose(error))
            {
                // Clear/shutdown owns the interrupted exit. Only cancellation-only,
                // uncommitted failures are expected; cleanup faults remain observable.
            }
            catch (Exception error) { ReportError(error); }
        }

        private static bool IsCancelledClose(UIOperationException error)
        {
            if (error.Committed || error.CleanupHandle.HasValue || !(error.InnerException is AggregateException aggregate)) { return false; }
            var failures = aggregate.Flatten().InnerExceptions;
            if (failures.Count == 0) { return false; }
            foreach (var failure in failures) { if (!(failure is OperationCanceledException)) { return false; } }
            return true;
        }

        private void OnDestroy()
        {
            _lifetime.Cancel();
            // Capture the host-owned Core. Never recreate a global Core while exiting.
            if (Core != null) { CloseAsync().Forget(Debug.LogException); }
        }
    }
}
