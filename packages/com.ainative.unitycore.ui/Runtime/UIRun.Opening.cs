using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        internal async UniTask<UIOpenResult<TView>> OpenAsync<TView>(UIWindowDefinition<TView> definition,
            CancellationToken cancellationToken, bool push) where TView : UIView
        {
            if (Committing) { return UIOpenResult<TView>.Rejected(UIError.ReentrantCall); }
            if (!Accepting) { return UIOpenResult<TView>.Rejected(UIError.NotRunning); }
            Windows.TryGetValue(typeof(TView), out var record);
            bool cached = record != null && record.Cached && !record.Opening && !record.Releasing;
            if (record != null && !cached) { return UIOpenResult<TView>.Rejected(UIError.AlreadyOpen); }
            if (push != (definition.Role == UIRole.Page))
            {
                return UIOpenResult<TView>.Rejected(UIError.InvalidRole);
            }
            UIError rejection = Navigation.CanOpen(definition.Role);
            if (rejection != UIError.None) { return UIOpenResult<TView>.Rejected(rejection); }
            cancellationToken.ThrowIfCancellationRequested();
            bool navigating = UINavigation.IsNavigation(definition.Role);
            var oldHandle = cached ? record.Handle : default;
            var oldClosing = cached ? record.Closing : null;
            if (cached)
            {
                Cache.Remove(record);
                record.Cached = false;
                record.Handle = new UIHandle(Owner, Generation, record.Handle.Instance, ++record.LastSession);
                record.Closing = null;
            }
            else
            {
                record = new UIWindowRecord(typeof(TView),
                new UIHandle(Owner, Generation, ++_nextInstance, 1), Scope.CreateScope("window:" + typeof(TView).Name),
                definition.Role, definition.OpaqueFullscreen, definition.AllowHudInput, definition.CachePolicy, definition.Transition);
                Windows.Add(typeof(TView), record);
            }
            record.Opening = true;
            if (navigating) { Navigation.Busy = true; }
            Pending++;
            bool committed = false;
            bool hadModal = Navigation.HasModal;
            UINavigation.Snapshot snapshot = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
            try
            {
                if (!cached) { record.Instance = await record.Scope.InstantiateAsync(definition.Address, Root.Staging, linked.Token); }
                linked.Token.ThrowIfCancellationRequested();
                record.View = record.Instance.Value.GetComponent<TView>();
                if (record.View == null) { throw new InvalidOperationException("Prefab does not contain the registered view."); }
                ValidateInputHierarchy(record.View);
                Commit(() =>
                {
                    if (navigating)
                    {
                        snapshot = Navigation.Capture();
                        record.ReturnSelection = snapshot.Selection;
                    }
                    record.Instance.Value.SetActive(false);
                    if (!cached) { record.Presenter = definition.PresenterFactory((TView)record.View, record.Scope); }
                    if (record.Presenter == null) { throw new InvalidOperationException("Presenter factory returned null."); }
                    linked.Token.ThrowIfCancellationRequested();
                    // A callback may partially bind before throwing; rollback must still unbind it.
                    if (!cached)
                    {
                        record.Bound = true;
                        record.Presenter.Bind();
                    }
                    linked.Token.ThrowIfCancellationRequested();
                    record.Resumed = true;
                    record.Presenter.Resume();
                    linked.Token.ThrowIfCancellationRequested();
                    if (!cached) { record.View.transform.SetParent(Root.Layer(definition.Role), false); }
                    else { record.View.transform.SetAsLastSibling(); }
                    linked.Token.ThrowIfCancellationRequested();
                    UISafeArea.Apply(record.View, UISafeArea.Normalize(UnityEngine.Screen.width,
                        UnityEngine.Screen.height, UnityEngine.Screen.safeArea), linked.Token);
                    record.View.CanvasGroup.interactable = false;
                    record.View.CanvasGroup.blocksRaycasts = false;
                    record.View.gameObject.SetActive(true);
                    linked.Token.ThrowIfCancellationRequested();
                    if (definition.Transition.DurationSeconds > 0)
                    {
                        record.Transitioning = true;
                        if (navigating) { Navigation.BeginTransition(record, linked.Token, entering: true); }
                        else { Navigation.RefreshInput(linked.Token); }
                    }
                });
                await PlayTransitionAsync(record, entering: true, linked.Token);
                Commit(() =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    EnsureAlive(record);
                    if (navigating) { Navigation.EndTransition(); }
                    record.Visible = true;
                    if (navigating) { Navigation.Open(record, linked.Token); }
                    else { Navigation.RefreshInput(linked.Token); }
                    EnsureAlive(record);
                    committed = true;
                });
                if (navigating) { NotifyModalChange(hadModal); }
                return UIOpenResult<TView>.Success((TView)record.View, record.Handle);
            }
            catch (Exception error)
            {
                if (committed)
                {
                    throw new UIOperationException("Open committed but notification failed.", error, true,
                        handle: Accepting && record.View != null ? record.Handle : (UIHandle?)null);
                }
                var failures = new List<Exception> { error };
                if (navigating) { Navigation.EndTransition(); }
                try { Commit(() => HideUncommitted(record)); }
                catch (Exception isolation) { failures.Add(isolation); }
                await RestoreNavigationAsync(snapshot, failures);
                if (!navigating && Accepting)
                {
                    try { Commit(() => Navigation.RefreshInput(Lifetime.Token)); }
                    catch (Exception input) { failures.Add(input); }
                }
                try
                {
                    if (cached && Accepting && failures.Count == 1 && record.View != null)
                    {
                        await StoreClosedAsync(record, restore: true);
                        if (record.Cached) { record.Handle = oldHandle; record.Closing = oldClosing; }
                    }
                    else { await BeginCleanup(record); }
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }
                if (failures.Count > 1)
                {
                    throw new UIOperationException("Open rollback failed.", new AggregateException(failures), false,
                        cleanupHandle: Windows.ContainsKey(record.ViewType) ? CleanupHandle(record) : (UIHandle?)null);
                }
                throw;
            }
            finally
            {
                Pending--;
                record.Opening = false;
                record.Transitioning = false;
                if (navigating) { Navigation.Busy = false; }
            }
        }

        private static void HideUncommitted(UIWindowRecord record)
        {
            record.Visible = false;
            if (record.View == null) { return; }
            if (record.View.CanvasGroup != null)
            {
                record.View.CanvasGroup.interactable = false;
                record.View.CanvasGroup.blocksRaycasts = false;
            }
            record.View.gameObject.SetActive(false);
        }

        private static void ValidateInputHierarchy(UIView view)
        {
            if (view.CanvasGroup == null || view.CanvasGroup.gameObject != view.gameObject || !view.CanvasGroup.enabled)
            {
                throw new InvalidOperationException("UIView requires its root CanvasGroup.");
            }
            if (view.SafeArea == null || view.SafeArea.parent != view.transform ||
                view.Visual == null || view.Visual == view.SafeArea || !view.Visual.IsChildOf(view.SafeArea))
            {
                throw new InvalidOperationException("UIView requires a direct SafeArea child and an independent Visual descendant.");
            }
            foreach (var group in view.GetComponentsInChildren<UnityEngine.CanvasGroup>(true))
            {
                if (group.ignoreParentGroups) { throw new InvalidOperationException("Window CanvasGroups cannot ignore parent input policy."); }
            }
            foreach (var canvas in view.GetComponentsInChildren<UnityEngine.Canvas>(true))
            {
                var gate = canvas.GetComponent<UICanvasInputGate>();
                if (gate != null) { continue; }
                gate = canvas.gameObject.AddComponent<UICanvasInputGate>();
                gate.Owner = view.CanvasGroup;
                UnityEngine.Debug.LogWarning("UI window contains an additional Canvas; review sorting and batching manually.", canvas);
            }
        }

        private void NotifyModalChange(bool previous)
        {
            if (previous == Navigation.HasModal) { return; }
            Commit(() =>
            {
                Lifetime.Token.ThrowIfCancellationRequested();
                _modalChanged(Navigation.HasModal, Lifetime.Token);
                Lifetime.Token.ThrowIfCancellationRequested();
            });
        }

        private async UniTask RestoreNavigationAsync(UINavigation.Snapshot snapshot, List<Exception> failures)
        {
            if (snapshot == null || !Accepting) { return; }
            try { Commit(() => Navigation.Restore(snapshot, Lifetime.Token)); }
            catch (Exception rollback)
            {
                failures.Add(rollback);
                bool hadModal = Navigation.HasModal;
                // A failed restore cannot leave a partly resumed stack as a usable navigation state.
                var isolated = Navigation.DetachAll();
                try { Commit(() =>
                {
                    foreach (var affected in isolated)
                    {
                        try { HideUncommitted(affected); }
                        catch (Exception isolation) { failures.Add(isolation); }
                    }
                    Root.SetModalTop(null);
                }); }
                catch (Exception isolation) { failures.Add(isolation); }
                foreach (var affected in isolated)
                {
                    try { await BeginCleanup(affected); }
                    catch (Exception cleanup) { failures.Add(cleanup); }
                }
                if (Accepting)
                {
                    try { Commit(() => { Root.SetModalTop(null); Navigation.RefreshInput(Lifetime.Token); }); }
                    catch (Exception cleanup) { failures.Add(cleanup); }
                    try { NotifyModalChange(hadModal); }
                    catch (Exception notification) { failures.Add(notification); }
                }
            }
        }

    }
}
