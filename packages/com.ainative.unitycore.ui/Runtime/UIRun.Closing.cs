using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        internal UniTask<UIResult> CloseAsync(UIHandle handle)
        {
            if (Committing) { return UniTask.FromResult(UIResult.Rejected(UIError.ReentrantCall)); }
            if (!Accepting) { return UniTask.FromResult(UIResult.Rejected(UIError.NotRunning)); }
            UIWindowRecord record = Find(handle);
            if (record == null || handle.CleanupOnly) { return UniTask.FromResult(UIResult.Rejected(UIError.InvalidHandle)); }
            if (record.Closing != null && record.Closing.Task.Status == UniTaskStatus.Pending) { return record.Closing.Task; }
            if (record.Opening || record.Releasing) { return UniTask.FromResult(UIResult.Rejected(UIError.WindowBusy)); }
            if (record.Cached)
            {
                if (record.Instance.IsAlive && record.View != null) { return UniTask.FromResult(UIResult.Success()); }
                var cleanup = BeginCleanup(record);
                record.Closing = record.Cleanup;
                return cleanup;
            }
            UIError rejection = Navigation.CanClose(record);
            if (rejection != UIError.None) { return UniTask.FromResult(UIResult.Rejected(rejection)); }
            var completion = new UniTaskCompletionSource<UIResult>();
            record.Closing = completion;
            CloseWindowAsync(record, completion).Forget();
            return completion.Task;
        }

        internal UniTask<UIResult> ReleaseAsync(UIHandle handle)
        {
            if (Committing) { return UniTask.FromResult(UIResult.Rejected(UIError.ReentrantCall)); }
            if (!Accepting) { return UniTask.FromResult(UIResult.Rejected(UIError.NotRunning)); }
            UIWindowRecord record = Find(handle);
            if (record == null) { return UniTask.FromResult(UIResult.Rejected(UIError.InvalidHandle)); }
            if (record.Opening || record.Visible || Navigation.Contains(record) ||
                (record.Closing != null && record.Closing.Task.Status == UniTaskStatus.Pending))
            { return UniTask.FromResult(UIResult.Rejected(UIError.WindowBusy)); }
            return BeginCleanup(record);
        }

        internal UniTask<UIResult> BackAsync()
        {
            if (Committing) { return UniTask.FromResult(UIResult.Rejected(UIError.ReentrantCall)); }
            if (!Accepting) { return UniTask.FromResult(UIResult.Rejected(UIError.NotRunning)); }
            if (Navigation.Busy) { return UniTask.FromResult(UIResult.Rejected(UIError.NavigationBusy)); }
            var target = Navigation.BackTarget();
            return target == null ? UniTask.FromResult(UIResult.Rejected(UIError.NoBackTarget)) : CloseAsync(target.Handle);
        }

        private async UniTask CloseWindowAsync(UIWindowRecord record, UniTaskCompletionSource<UIResult> completion)
        {
            bool navigating = Navigation.Contains(record);
            if (navigating) { Navigation.Busy = true; }
            Pending++;
            bool committed = false;
            bool hadModal = Navigation.HasModal;
            UINavigation.Snapshot snapshot = null;
            Exception failure = null;
            try
            {
                if (record.TransitionOptions.DurationSeconds > 0 && record.Instance.IsAlive && record.View != null)
                {
                    Commit(() =>
                    {
                        record.Transitioning = true;
                        if (navigating)
                        {
                            snapshot = Navigation.Capture();
                            Navigation.PrepareClose(record, Lifetime.Token);
                        }
                        else { Navigation.RefreshInput(Lifetime.Token); }
                    });
                    await PlayTransitionAsync(record, entering: false, Lifetime.Token);
                }
                Commit(() =>
                {
                    Lifetime.Token.ThrowIfCancellationRequested();
                    if (navigating)
                    {
                        Navigation.EndTransition();
                        if (snapshot == null) { snapshot = Navigation.Capture(); }
                        Navigation.Close(record, Lifetime.Token);
                    }
                    record.Visible = false;
                    committed = true;
                });
                var failures = new List<Exception>();
                if (!navigating)
                {
                    try { Commit(() => Navigation.RefreshInput(Lifetime.Token)); }
                    catch (Exception input) { failures.Add(input); }
                }
                try { await StoreClosedAsync(record); }
                catch (Exception cleanup) { failures.Add(cleanup); }
                try { if (navigating) { NotifyModalChange(hadModal); } }
                catch (Exception notification) { failures.Add(notification); }
                if (failures.Count != 0) { throw new AggregateException(failures); }
            }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                record.Transitioning = false;
                if (navigating) { Navigation.EndTransition(); }
                if (!committed) { await RestoreNavigationAsync(snapshot, failures); }
                if (!committed && !navigating && Accepting)
                {
                    try { Commit(() => Navigation.RefreshInput(Lifetime.Token)); }
                    catch (Exception input) { failures.Add(input); }
                }
                failure = new UIOperationException("Window close failed.", new AggregateException(failures), committed,
                    handle: !committed && Accepting && record.View != null ? record.Handle : (UIHandle?)null,
                    cleanupHandle: record.CleanupFailed ? CleanupHandle(record) : FindCleanupHandle(error));
                if (!committed) { record.Closing = null; }
            }
            finally
            {
                Pending--;
                record.Transitioning = false;
                if (navigating) { Navigation.Busy = false; }
            }
            // Awaiters can start another navigation immediately; finish outside both guards.
            if (failure == null) { completion.TrySetResult(UIResult.Success()); }
            else { completion.TrySetException(failure); }
        }

        internal UIResult BringToFront(UIHandle handle)
        {
            if (Committing) { return UIResult.Rejected(UIError.ReentrantCall); }
            if (!Accepting) { return UIResult.Rejected(UIError.NotRunning); }
            var record = Find(handle);
            if (record == null || handle.CleanupOnly) { return UIResult.Rejected(UIError.InvalidHandle); }
            if (record.Role == UIRole.Page) { return UIResult.Rejected(UIError.InvalidRole); }
            if (!record.Visible || record.Transitioning || record.Closing != null) { return UIResult.Rejected(UIError.WindowBusy); }
            if (record.Role == UIRole.Modal)
            {
                if (Navigation.Busy) { return UIResult.Rejected(UIError.NavigationBusy); }
                if (Navigation.IsTopModal(record)) { return UIResult.Success(); }
                Navigation.Busy = true;
                bool committed = false;
                var snapshot = Navigation.Capture();
                try
                {
                    Commit(() =>
                    {
                        Navigation.BringModalToFront(record, Lifetime.Token);
                        committed = true;
                    });
                }
                catch (Exception error)
                {
                    var failures = new List<Exception> { error };
                    if (!committed && Accepting)
                    {
                        try { Commit(() => Navigation.Restore(snapshot, Lifetime.Token)); }
                        catch (Exception rollback)
                        {
                            failures.Add(rollback);
                            // A synchronous reorder cannot await scope cleanup. Retire this run;
                            // Core.CloseAsync remains responsible for draining its Assets scope.
                            try { Retire(); }
                            catch (Exception retirement) { failures.Add(retirement); }
                        }
                    }
                    throw new UIOperationException("Modal reorder failed.", new AggregateException(failures), committed,
                        handle: Accepting && record.View != null ? record.Handle : (UIHandle?)null);
                }
                finally { Navigation.Busy = false; }
            }
            else { Commit(() => record.View.transform.SetAsLastSibling()); }
            return UIResult.Success();
        }

    }
}
