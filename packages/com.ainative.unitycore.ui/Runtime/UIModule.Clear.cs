using System;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    public sealed partial class UIModule
    {
        private UniTaskCompletionSource<UIResult> _clearing;
        private UIRun _clearOwner;
        private bool _clearModalPending;

        public UniTask<UIResult> ClearAsync()
        {
            var run = _run;
            if (run == null) { return StoppedResult(); }
            if (run.Committing) { return UniTask.FromResult(UIResult.Rejected(UIError.ReentrantCall)); }
            if (_stopping) { return StoppedResult(); }
            if (_clearOwner == run && _clearing != null) { return _clearing.Task; }
            var completion = new UniTaskCompletionSource<UIResult>();
            _clearOwner = run;
            _clearing = completion;
            ClearRunAsync(run, completion).Forget();
            return completion.Task;
        }

        private async UniTask ClearRunAsync(UIRun run, UniTaskCompletionSource<UIResult> completion)
        {
            Exception failure = null;
            _clearModalPending |= run.Navigation.HasModal;
            try
            {
                await run.ShutdownAsync(default);
                await run.Scope.CloseAsync();
                if (_run != run || _stopping) { throw new OperationCanceledException("Core stopped during Clear."); }
                run.Retire();
                var next = new UIRun(this, ++_generation, _options, _assets, _eventSystem, NotifyModalStateChanged, _time);
                _run = next;
                try
                {
                    if (_clearModalPending) { next.Commit(() => NotifyModalStateChanged(false, next.Lifetime.Token)); }
                    _clearModalPending = false;
                }
                catch (Exception notification)
                {
                    // A notification failure must not authorize replacing the old business model.
                    try { await next.ShutdownAsync(default); }
                    catch (Exception cleanup) { throw new AggregateException(notification, cleanup); }
                    throw;
                }
            }
            catch (Exception error) { failure = new UIOperationException("UI clear failed; retry before replacing model dependencies.", error, true); }
            finally
            {
                if (ReferenceEquals(_clearing, completion)) { _clearing = null; _clearOwner = null; }
            }
            if (failure == null) { completion.TrySetResult(UIResult.Success()); }
            else { completion.TrySetException(failure); }
        }
    }
}
