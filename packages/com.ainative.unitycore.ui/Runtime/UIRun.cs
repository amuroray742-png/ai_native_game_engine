using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        internal readonly object Owner;
        internal readonly long Generation;
        internal readonly UIRoot Root;
        internal readonly AssetScope Scope;
        internal readonly Dictionary<Type, UIWindowRecord> Windows = new Dictionary<Type, UIWindowRecord>();
        internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        internal readonly UINavigation Navigation;
        internal readonly UIWindowCache Cache;
        private readonly Action<bool, CancellationToken> _modalChanged;
        internal bool Accepting = true;
        internal bool Committing;
        internal int Pending;
        private long _nextInstance;
        private bool _retirementPending;
        private bool _retired;

        internal UIRun(object owner, long generation, UIOptions options, IAssets assets,
            UnityEngine.EventSystems.EventSystem events, Action<bool, CancellationToken> modalChanged,
            AiNative.UnityCore.TimeMachine.ITimeMachine time)
        {
            Owner = owner;
            Generation = generation;
            Scope = assets.CreateScope("ui:" + generation);
            Root = new UIRoot(options);
            Navigation = new UINavigation(this, events, options.MaxPageDepth);
            Cache = new UIWindowCache(options.ClosedCacheCapacity);
            _modalChanged = modalChanged;
            Time = time;
            _screenTicket = time.RegisterUpdate(UpdateScreen, "UI.SafeArea");
        }

        internal void Commit(Action action)
        {
            bool wasCommitting = Committing;
            Committing = true;
            Exception failure = null;
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Committing = wasCommitting; }
            // Core may retire synchronously inside a Presenter or Unity callback.
            // Finish that callback before invoking its cleanup, without waiting for a frame.
            if (!Committing && _retirementPending)
            {
                _retirementPending = false;
                try { UnbindRetiredWindows(); }
                catch (Exception cleanup)
                {
                    failure = failure == null ? cleanup : new AggregateException(failure, cleanup);
                }
            }
            if (!Committing)
            {
                while (_completions.Count != 0) { _completions.Dequeue()(); }
            }
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }

        private UniTask<UIResult> BeginCleanup(UIWindowRecord record)
        {
            if (record.Cleanup != null && record.Cleanup.Task.Status != UniTaskStatus.Faulted)
            {
                return record.Cleanup.Task;
            }
            var completion = new UniTaskCompletionSource<UIResult>();
            record.Cleanup = completion;
            FinishCleanupAsync(record, completion).Forget();
            return completion.Task;
        }

        private async UniTask FinishCleanupAsync(UIWindowRecord record, UniTaskCompletionSource<UIResult> completion)
        {
            Pending++;
            Exception failure = null;
            try
            {
                await CleanAsync(record);
            }
            catch (Exception error)
            {
                failure = new UIOperationException("Window cleanup failed.", error, true,
                    cleanupHandle: CleanupHandle(record));
            }
            finally { Pending--; }
            if (failure == null) { completion.TrySetResult(UIResult.Success()); }
            else { completion.TrySetException(failure); }
        }

        private async UniTask CleanAsync(UIWindowRecord record)
        {
            record.Releasing = true;
            record.Cached = false;
            record.Visible = false;
            var failures = new List<Exception>();
            UnbindWindow(record, failures);
            if (!record.ScopeClosed)
            {
                try { await record.Scope.CloseAsync(); record.ScopeClosed = true; }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0)
            {
                record.CleanupFailed = true;
                throw new AggregateException(failures);
            }
            record.CleanupFailed = false;
            Cache.Remove(record);
            Navigation.Remove(record);
            if (Windows.TryGetValue(record.ViewType, out var current) && ReferenceEquals(current, record))
            { Windows.Remove(record.ViewType); }
        }

        private void UnbindWindow(UIWindowRecord record, List<Exception> failures)
        {
            record.Visible = false;
            Commit(() =>
            {
                if (record.Resumed)
                {
                    try { record.Presenter.Suspend(); record.Resumed = false; }
                    catch (Exception error) { failures.Add(error); }
                }
                if (record.View != null) { record.View.gameObject.SetActive(false); }
                // Unbind owns separate subscriptions and must run even when Suspend failed.
                if (record.Bound)
                {
                    try { record.Presenter.Unbind(); record.Bound = false; }
                    catch (Exception error) { failures.Add(error); }
                }
            });
        }

        internal void Retire()
        {
            if (_retired) { return; }
            _retired = true;
            Accepting = false;
            StopScreenUpdates();
            Navigation.DetachAll();
            _retirementPending = true;
            var failures = new List<Exception>();
            try { Lifetime.Cancel(); }
            catch (Exception error) { failures.Add(error); }
            // This also runs deferred unbinding when retirement was not called from a callback.
            try { Commit(Root.Destroy); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) { throw new AggregateException(failures); }
        }

        private void UnbindRetiredWindows()
        {
            var failures = new List<Exception>();
            foreach (var record in new List<UIWindowRecord>(Windows.Values))
            {
                UnbindWindow(record, failures);
            }
            // Assets.Shut owns forced resource destruction. No PlayerLoop work is needed here.
            if (failures.Count != 0) { throw new AggregateException(failures); }
        }

        internal async UniTask ShutdownAsync(CancellationToken forcedExit)
        {
            Accepting = false;
            StopScreenUpdates();
            var failures = new List<Exception>();
            try { Lifetime.Cancel(); }
            catch (Exception error) { failures.Add(error); }
            // Disable all input and rendering before any pending operation or scope can yield.
            // Retire may already have destroyed the root after an unrecoverable reorder.
            if (!_retired)
            {
                try { Commit(() => Root.Object.SetActive(false)); }
                catch (Exception isolation) { failures.Add(isolation); }
            }
            while (Pending != 0)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, forcedExit, cancelImmediately: true);
            }
            Navigation.DetachAll();
            foreach (var record in new List<UIWindowRecord>(Windows.Values))
            {
                try { await BeginCleanup(record).AttachExternalCancellation(forcedExit); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) { throw new AggregateException(failures); }
        }

        internal UIWindowRecord Find(UIHandle handle)
        {
            if (!ReferenceEquals(handle.Owner, Owner) || handle.Generation != Generation) { return null; }
            foreach (var record in Windows.Values)
            {
                if (record.Handle.Instance == handle.Instance && record.Handle.Session == handle.Session) { return record; }
            }
            return null;
        }

        private UIHandle CleanupHandle(UIWindowRecord record) =>
            new UIHandle(Owner, Generation, record.Handle.Instance, record.Handle.Session, true);
    }
}
