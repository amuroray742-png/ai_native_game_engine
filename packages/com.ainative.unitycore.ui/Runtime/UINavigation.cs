using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI
{
    // Owns navigation history. Window resource ownership remains in UIRun.
    internal sealed class UINavigation
    {
        private readonly UIRun _run;
        private readonly EventSystem _events;
        private readonly int _maxDepth;
        private readonly List<UIWindowRecord> _pages = new List<UIWindowRecord>();
        private readonly List<UIWindowRecord> _modals = new List<UIWindowRecord>();
        private GameObject _beforeModalsSelection;
        internal bool Busy;
        private bool _transitioning;
        internal int PageDepth => _pages.Count;
        internal int ModalCount => _modals.Count;
        internal bool HasModal => _modals.Count != 0;
        internal bool IsTopModal(UIWindowRecord record) => Top(_modals) == record;

        internal UINavigation(UIRun run, EventSystem events, int maxDepth)
        {
            _run = run;
            _events = events;
            _maxDepth = maxDepth;
        }

        internal bool Contains(UIWindowRecord record) => _pages.Contains(record) || _modals.Contains(record);
        internal static bool IsNavigation(UIRole role) => role == UIRole.Page || role == UIRole.Modal;
        internal UIError CanOpen(UIRole role)
        {
            if (!IsNavigation(role)) { return UIError.None; }
            if (Busy) { return UIError.NavigationBusy; }
            if (role == UIRole.Page && _pages.Count >= _maxDepth) { return UIError.StackLimitReached; }
            return UIError.None;
        }

        internal UIError CanClose(UIWindowRecord record)
        {
            if (!IsNavigation(record.Role)) { return UIError.None; }
            if (Busy) { return UIError.NavigationBusy; }
            if (record.Role == UIRole.Page && _pages.Contains(record))
            {
                if (Top(_pages) != record) { return UIError.NotTopPage; }
                if (_pages.Count == 1) { return UIError.NoBackTarget; }
            }
            return UIError.None;
        }

        internal UIWindowRecord BackTarget() => HasModal ? Top(_modals) : _pages.Count > 1 ? Top(_pages) : null;

        internal Snapshot Capture() => new Snapshot(_pages, _modals, _events.currentSelectedGameObject, _beforeModalsSelection);

        internal void Open(UIWindowRecord candidate, CancellationToken token)
        {
            var pages = new List<UIWindowRecord>(_pages);
            var modals = new List<UIWindowRecord>(_modals);
            if (candidate.Role == UIRole.Page) { pages.Add(candidate); }
            else { modals.Add(candidate); }
            bool firstModal = candidate.Role == UIRole.Modal && !HasModal;
            Apply(pages, modals, DefaultSelection(candidate), token);
            if (candidate.View == null || !candidate.Instance.IsAlive)
            { throw new InvalidOperationException("Window was destroyed by a navigation callback."); }
            Replace(pages, modals);
            if (firstModal) { _beforeModalsSelection = candidate.ReturnSelection; }
        }

        internal void Close(UIWindowRecord record, CancellationToken token)
        {
            var pages = new List<UIWindowRecord>(_pages);
            var modals = new List<UIWindowRecord>(_modals);
            pages.Remove(record);
            modals.Remove(record);
            GameObject preferred = HasModal && modals.Count == 0 ? _beforeModalsSelection : record.ReturnSelection;
            Apply(pages, modals, preferred, token);
            Replace(pages, modals);
            if (modals.Count == 0) { _beforeModalsSelection = null; }
        }

        internal void BringModalToFront(UIWindowRecord record, CancellationToken token)
        {
            var modals = new List<UIWindowRecord>(_modals);
            modals.Remove(record);
            modals.Add(record);
            Apply(_pages, modals, null, token);
            Replace(_pages, modals);
        }

        internal void Restore(Snapshot snapshot, CancellationToken token)
        {
            Apply(snapshot.Pages, snapshot.Modals, snapshot.Selection, token);
            Replace(snapshot.Pages, snapshot.Modals);
            _beforeModalsSelection = snapshot.BeforeModalsSelection;
        }

        internal void Remove(UIWindowRecord record)
        {
            _pages.Remove(record);
            _modals.Remove(record);
        }

        internal List<UIWindowRecord> DetachAll()
        {
            var records = new List<UIWindowRecord>(_pages);
            records.AddRange(_modals);
            _pages.Clear();
            _modals.Clear();
            _beforeModalsSelection = null;
            return records;
        }

        internal void RefreshInput(CancellationToken token) => ApplyInput(_pages, _modals, null, token);

        internal void BeginTransition(UIWindowRecord candidate, CancellationToken token, bool entering)
        {
            RememberSelection();
            _transitioning = true;
            if (entering && candidate.Role == UIRole.Modal) { _run.Root.SetModalTop(candidate.View); }
            RefreshInput(token);
        }

        internal void EndTransition() { _transitioning = false; }

        internal void PrepareClose(UIWindowRecord outgoing, CancellationToken token)
        {
            // Reveal the destination before the exit animation, but preserve history
            // and keep the outgoing window visible until Close commits.
            if (outgoing.Role == UIRole.Page)
            {
                for (int i = _pages.Count - 1; i >= 0; i--)
                {
                    var page = _pages[i];
                    if (page == outgoing) { continue; }
                    SetVisible(page, true, token);
                    if (page.OpaqueFullscreen) { break; }
                }
            }
            BeginTransition(outgoing, token, entering: false);
        }

        private void Apply(IReadOnlyList<UIWindowRecord> pages, IReadOnlyList<UIWindowRecord> modals,
            GameObject preferredSelection, CancellationToken token)
        {
            pages = Alive(pages);
            modals = Alive(modals);
            RememberSelection();
            var visible = new HashSet<UIWindowRecord>();
            for (int i = pages.Count - 1; i >= 0; i--)
            {
                visible.Add(pages[i]);
                if (pages[i].OpaqueFullscreen) { break; }
            }
            foreach (var modal in modals) { visible.Add(modal); }
            var affected = new List<UIWindowRecord>(_pages);
            affected.AddRange(_modals);
            foreach (var page in pages) { if (!affected.Contains(page)) { affected.Add(page); } }
            foreach (var modal in modals) { if (!affected.Contains(modal)) { affected.Add(modal); } }
            // Restore the incoming page before hiding the outgoing one.
            foreach (var record in affected)
            {
                if (visible.Contains(record)) { SetVisible(record, true, token); }
            }
            foreach (var record in affected)
            {
                if (!visible.Contains(record)) { SetVisible(record, false, token); }
            }
            foreach (var modal in modals) { modal.View.transform.SetAsLastSibling(); }
            _run.Root.SetModalTop(Top(modals)?.View);
            token.ThrowIfCancellationRequested();
            ApplyInput(pages, modals, preferredSelection, token);
        }

        private static void SetVisible(UIWindowRecord record, bool visible, CancellationToken token)
        {
            if (record.View == null || !record.Instance.IsAlive) { return; }
            if (visible && (!record.Resumed || record.NeedsResume))
            {
                record.Resumed = true;
                record.NeedsResume = false;
                record.Presenter.Resume();
                token.ThrowIfCancellationRequested();
            }
            else if (!visible && record.Resumed)
            {
                // A failed Suspend may already have removed subscriptions: rollback must Resume.
                record.NeedsResume = true;
                record.Presenter.Suspend();
                record.Resumed = false;
                record.NeedsResume = false;
                token.ThrowIfCancellationRequested();
            }
            record.View.gameObject.SetActive(visible);
            token.ThrowIfCancellationRequested();
            record.Visible = visible;
        }

        private void ApplyInput(IReadOnlyList<UIWindowRecord> pages, IReadOnlyList<UIWindowRecord> modals,
            GameObject preferredSelection, CancellationToken token)
        {
            var page = Top(pages);
            var modal = Top(modals);
            foreach (var record in _run.Windows.Values)
            {
                if (record.View == null || !record.Bound) { continue; }
                bool allowed = !_transitioning && !record.Transitioning &&
                    record.Visible && record.Role != UIRole.Overlay &&
                    (modal != null ? record == modal : record.Role == UIRole.Hud
                        ? page == null || page.AllowHudInput : record == page);
                record.View.CanvasGroup.interactable = allowed;
                record.View.CanvasGroup.blocksRaycasts = allowed;
            }
            UIWindowRecord target = modal ?? page;
            GameObject selection = null;
            var candidates = new[] { preferredSelection, _events.currentSelectedGameObject,
                target?.LastSelection, DefaultSelection(target) };
            foreach (var candidate in candidates)
            {
                if (!IsValidSelection(candidate)) { continue; }
                selection = candidate;
                break;
            }
            if (_events.currentSelectedGameObject != selection) { _events.SetSelectedGameObject(selection); }
            token.ThrowIfCancellationRequested();
        }

        private bool IsValidSelection(GameObject selection)
        {
            if (selection == null || !selection.activeInHierarchy) { return false; }
            var selectable = selection.GetComponent<Selectable>();
            if (selectable == null || !selectable.IsActive() || !selectable.IsInteractable()) { return false; }
            foreach (var record in _run.Windows.Values)
            {
                if (record.Visible && record.View != null && record.View.CanvasGroup.interactable &&
                    selection.transform.IsChildOf(record.View.transform)) { return true; }
            }
            return false;
        }

        private static GameObject DefaultSelection(UIWindowRecord record)
        {
            if (record == null || record.View == null || record.View.DefaultSelection == null) { return null; }
            return record.View.DefaultSelection.gameObject;
        }

        private void RememberSelection()
        {
            GameObject selection = _events.currentSelectedGameObject;
            if (selection == null) { return; }
            foreach (var record in _run.Windows.Values)
            {
                if (record.View != null && selection.transform.IsChildOf(record.View.transform))
                {
                    record.LastSelection = selection;
                    return;
                }
            }
        }

        private void Replace(IReadOnlyList<UIWindowRecord> pages, IReadOnlyList<UIWindowRecord> modals)
        {
            var livePages = Alive(pages);
            var liveModals = Alive(modals);
            _pages.Clear(); _pages.AddRange(livePages);
            _modals.Clear(); _modals.AddRange(liveModals);
        }

        private static List<UIWindowRecord> Alive(IReadOnlyList<UIWindowRecord> records)
        {
            var result = new List<UIWindowRecord>();
            foreach (var record in records)
            {
                if (record.View != null && record.Instance.IsAlive) { result.Add(record); }
            }
            return result;
        }

        private static UIWindowRecord Top(IReadOnlyList<UIWindowRecord> records) => records.Count == 0 ? null : records[records.Count - 1];

        internal sealed class Snapshot
        {
            internal readonly List<UIWindowRecord> Pages;
            internal readonly List<UIWindowRecord> Modals;
            internal readonly GameObject Selection;
            internal readonly GameObject BeforeModalsSelection;
            internal Snapshot(List<UIWindowRecord> pages, List<UIWindowRecord> modals, GameObject selection,
                GameObject beforeModalsSelection)
            {
                Pages = new List<UIWindowRecord>(pages);
                Modals = new List<UIWindowRecord>(modals);
                Selection = selection;
                BeforeModalsSelection = beforeModalsSelection;
            }
        }
    }
}
