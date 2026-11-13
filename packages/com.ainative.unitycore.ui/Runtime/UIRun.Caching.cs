using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        private async UniTask StoreClosedAsync(UIWindowRecord record, bool restore = false)
        {
            if (!Accepting || record.CachePolicy != UICachePolicy.Cache || Cache.Capacity == 0 ||
                record.Instance == null || !record.Instance.IsAlive || record.View == null)
            {
                await BeginCleanup(record);
                return;
            }
            try
            {
                Commit(() =>
                {
                    if (record.Resumed) { record.Presenter.Suspend(); record.Resumed = false; }
                    HideUncommitted(record);
                });
                while (Accepting && Cache.Count >= Cache.Capacity)
                {
                    if (restore && record.CacheOrder < Cache.Oldest.CacheOrder)
                    {
                        await BeginCleanup(record);
                        return;
                    }
                    await BeginCleanup(Cache.Oldest);
                }
                if (Accepting && record.Instance.IsAlive && record.View != null) { Cache.Add(record, restore); }
                else { await BeginCleanup(record); }
            }
            catch (Exception error)
            {
                try { await BeginCleanup(record); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        internal async UniTask<UIResult> TrimAsync()
        {
            if (Committing) { return UIResult.Rejected(UIError.ReentrantCall); }
            if (!Accepting) { return UIResult.Rejected(UIError.NotRunning); }
            var candidates = Cache.Snapshot();
            var handles = new UIHandle[candidates.Length];
            for (int i = 0; i < candidates.Length; i++) { handles[i] = candidates[i].Handle; }
            var failures = new List<Exception>();
            Pending++;
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    var record = candidates[i];
                    if (!Cache.Contains(record) || record.Opening || !record.Handle.Equals(handles[i])) { continue; }
                    try { await BeginCleanup(record); }
                    catch (Exception error) { failures.Add(error); }
                }
                if (failures.Count != 0)
                {
                    var error = new AggregateException(failures);
                    throw new UIOperationException("Cache trim failed.", error, true, cleanupHandle: FindCleanupHandle(error));
                }
                return UIResult.Success();
            }
            finally { Pending--; }
        }

        private static UIHandle? FindCleanupHandle(Exception error)
        {
            if (error is UIOperationException operation && operation.CleanupHandle.HasValue) { return operation.CleanupHandle; }
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var handle = FindCleanupHandle(inner);
                    if (handle.HasValue) { return handle; }
                }
            }
            return error.InnerException == null ? null : FindCleanupHandle(error.InnerException);
        }
    }
}
