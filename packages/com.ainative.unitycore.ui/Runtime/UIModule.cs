using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using UnityEngine.EventSystems;

namespace AiNative.UnityCore.UI
{
    public sealed partial class UIModule : IUI, IModule, IAsyncShutdownModule
    {
        private readonly UIOptions _options;
        private readonly EventSystem _eventSystem;
        private readonly Dictionary<Type, object> _definitions = new Dictionary<Type, object>();
        private AiNative.UnityCore.Assets.IAssets _assets;
        private UIRun _run;
        private long _generation;
        private bool _stopping;
        private AiNative.UnityCore.TimeMachine.ITimeMachine _time;

        public UIModule(UIOptions options, EventSystem eventSystem)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (eventSystem == null) throw new ArgumentNullException(nameof(eventSystem));
            _eventSystem = eventSystem;
        }

        public string GetModuleName() => "AiNative.UnityCore.UI";
        public void OnRegister(ICore core) { core.RegisterInterface<IUI, UIModule>(this); }
        public UniTask<UniCoreException> Initialize(ICore core, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UniTask.FromResult<UniCoreException>(null);
        }
        public void AfterInitialize(ICore core) { _assets = core.GetModule<AiNative.UnityCore.Assets.IAssets>(); }
        public UniTask<UniCoreException> Start(ICore core, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stopping = false;
            _clearModalPending = false;
            _time = core.TimeMachine;
            _run = new UIRun(this, ++_generation, _options, _assets, _eventSystem, NotifyModalStateChanged, core.TimeMachine);
            return UniTask.FromResult<UniCoreException>(null);
        }
        public void AfterStart(ICore core) { }
        public async UniTask ShutdownAsync(ICore core, CancellationToken forcedExit)
        {
            _stopping = true;
            var run = _run;
            if (run == null) { return; }
            await run.ShutdownAsync(forcedExit);
            await run.Scope.CloseAsync().AttachExternalCancellation(forcedExit);
        }
        public void Shut(ICore core)
        {
            _stopping = true;
            var run = _run;
            _run = null;
            ModalStateChanged = null;
            if (run == null) { return; }
            run.Retire();
        }

        public UIDiagnostics GetDiagnostics()
        {
            var run = _run;
            if (run == null)
            {
                return new UIDiagnostics(0, 0, 0, 0, 0, 0);
            }
            return run.Diagnostics();
        }
        public bool HasModal => _run != null && _run.Navigation.HasModal;
        public event Action<bool> ModalStateChanged;

        private void NotifyModalStateChanged(bool hasModal, CancellationToken lifetime)
        {
            var subscribers = ModalStateChanged;
            if (subscribers == null) { return; }
            var failures = new List<Exception>();
            foreach (Action<bool> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(hasModal); }
                catch (Exception error) { failures.Add(error); }
                if (lifetime.IsCancellationRequested)
                {
                    failures.Add(new OperationCanceledException(lifetime));
                    break;
                }
            }
            if (failures.Count != 0) { throw new AggregateException(failures); }
        }

        public UniTask<UIOpenResult<TView>> OpenAsync<TView>(CancellationToken cancellationToken = default)
            where TView : UIView
            => OpenWindowAsync<TView>(false, cancellationToken);

        public UniTask<UIOpenResult<TView>> PushAsync<TView>(CancellationToken cancellationToken = default)
            where TView : UIView
            => OpenWindowAsync<TView>(true, cancellationToken);

        private UniTask<UIOpenResult<TView>> OpenWindowAsync<TView>(bool push, CancellationToken cancellationToken)
            where TView : UIView
        {
            var run = _run;
            if (run == null) { return UniTask.FromResult(UIOpenResult<TView>.Rejected(UIError.NotRunning)); }
            if (run.Committing) { return UniTask.FromResult(UIOpenResult<TView>.Rejected(UIError.ReentrantCall)); }
            if (!_definitions.TryGetValue(typeof(TView), out var definition))
            {
                return UniTask.FromResult(UIOpenResult<TView>.Rejected(UIError.NotRegistered));
            }
            return run.OpenAsync((UIWindowDefinition<TView>)definition, cancellationToken, push);
        }
        public UniTask<UIResult> CloseAsync(UIHandle handle) => _run == null ? StoppedResult() : _run.CloseAsync(handle);
        public UniTask<UIResult> ReleaseAsync(UIHandle handle) => _run == null ? StoppedResult() : _run.ReleaseAsync(handle);
        public UniTask<UIResult> BackAsync() => _run == null ? StoppedResult() : _run.BackAsync();
        public UniTask<UIResult> TrimAsync() => _run == null ? StoppedResult() : _run.TrimAsync();
        public UIResult BringToFront(UIHandle handle) => _run == null ? UIResult.Rejected(UIError.NotRunning) : _run.BringToFront(handle);
        private UniTask<UIResult> UnsupportedResult() => UniTask.FromResult(UIResult.Rejected(
            _run != null && _run.Committing ? UIError.ReentrantCall : UIError.NotRunning));
        public bool TryGet<TView>(out TView view, out UIHandle handle) where TView : UIView
        {
            view = null;
            handle = default;
            var run = _run;
            if (run == null || !run.Accepting || run.Committing)
            {
                return false;
            }
            if (!run.Windows.TryGetValue(typeof(TView), out var record) || !record.Visible ||
                record.Opening || record.Transitioning || record.Closing != null || record.View == null)
            {
                return false;
            }
            view = (TView)record.View;
            handle = record.Handle;
            return true;
        }

        private static UniTask<UIResult> StoppedResult() => UniTask.FromResult(UIResult.Rejected(UIError.NotRunning));

        public void Register<TView>(UIWindowDefinition<TView> definition) where TView : UIView
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            if (_run != null)
            {
                throw new InvalidOperationException("Window definitions can only be registered while stopped.");
            }
            if (_definitions.ContainsKey(typeof(TView)))
            {
                throw new InvalidOperationException("A definition is already registered for this view type.");
            }
            _definitions.Add(typeof(TView), definition);
        }
    }
}
