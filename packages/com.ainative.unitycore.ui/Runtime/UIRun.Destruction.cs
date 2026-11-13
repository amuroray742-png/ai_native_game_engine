using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        internal string LastRecoveryError;
        private bool _recovering;

        private void ObserveDestroyedWindows()
        {
            if (!Accepting || Committing || Navigation.Busy || _recovering) { return; }
            var destroyed = new List<UIWindowRecord>();
            foreach (var record in Windows.Values)
            {
                if (record.Instance != null && (!record.Instance.IsAlive || record.View == null) &&
                    !record.Opening && !record.Releasing &&
                    (record.Closing == null || record.Closing.Task.Status != UniTaskStatus.Pending))
                { destroyed.Add(record); }
            }
            if (destroyed.Count != 0) { RecoverDestroyedAsync(destroyed).Forget(); }
        }

        private async UniTask RecoverDestroyedAsync(List<UIWindowRecord> destroyed)
        {
            _recovering = true;
            Navigation.Busy = true;
            Pending++;
            var failures = new List<Exception>();
            bool hadModal = Navigation.HasModal;
            try
            {
                var snapshot = Navigation.Capture();
                // Restore filters dead instances before calculating visibility and focus.
                await RestoreNavigationAsync(snapshot, failures);
                bool restored = failures.Count == 0;
                foreach (var record in destroyed)
                {
                    if (!Windows.TryGetValue(record.ViewType, out var current) || !ReferenceEquals(current, record)) { continue; }
                    try { await BeginCleanup(record); }
                    catch (Exception error) { failures.Add(error); }
                }
                if (Accepting && restored)
                {
                    try { NotifyModalChange(hadModal); }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                if (failures.Count != 0) { LastRecoveryError = new AggregateException(failures).ToString(); }
                Pending--;
                Navigation.Busy = false;
                _recovering = false;
            }
        }

        private static void EnsureAlive(UIWindowRecord record)
        {
            if (record.Instance == null || !record.Instance.IsAlive || record.View == null)
            { throw new InvalidOperationException("Window was externally destroyed before operation commit."); }
        }
    }
}
