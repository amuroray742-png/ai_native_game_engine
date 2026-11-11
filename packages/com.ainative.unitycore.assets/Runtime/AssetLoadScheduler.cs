using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    // Only native work occupies a slot; dependency waits must remain outside this queue.
    internal sealed class AssetLoadScheduler
    {
        private sealed class Work
        {
            internal Func<AssetPriority> Priority;
            internal Func<bool> Wanted;
            internal Action Start;
            internal Action Cancel;
        }
        private readonly List<Work> _queue = new List<Work>();
        private readonly AssetRuntimeOptions _options;
        private bool _pumping;
        private int _preferredStarts;
        private int _frame = -1;
        private int _created;
        internal int Active { get; private set; }
        internal int Peak { get; private set; }
        internal int Queued => _queue.Count;

        internal AssetLoadScheduler(AssetRuntimeOptions options) { _options = options; }

        internal UniTask<T> Run<T>(Func<UniTask<T>> operation, Func<AssetPriority> priority, Func<bool> wanted)
        {
            var completion = new UniTaskCompletionSource<T>();
            _queue.Add(new Work
            {
                Priority = priority, Wanted = wanted,
                Start = () => Execute(operation, completion).Forget(),
                Cancel = () => completion.TrySetCanceled()
            });
            Pump();
            return completion.Task;
        }

        internal void Pump()
        {
            if (_pumping) { return; }
            _pumping = true;
            try
            {
                foreach (var work in _queue.ToArray())
                {
                    if (!work.Wanted()) { _queue.Remove(work); work.Cancel(); }
                }
                while (Active < _options.MaxConcurrentLoads && _queue.Count != 0)
                {
                    var oldest = _queue[0];
                    var selected = _preferredStarts >= 8 ? oldest : _queue.OrderByDescending(x => x.Priority()).First();
                    _preferredStarts = selected == oldest ? 0 : _preferredStarts + 1;
                    _queue.Remove(selected);
                    if (!selected.Wanted()) { selected.Cancel(); continue; }
                    Active++;
                    Peak = Math.Max(Peak, Active);
                    selected.Start();
                }
            }
            finally { _pumping = false; }
        }

        private async UniTask Execute<T>(Func<UniTask<T>> operation, UniTaskCompletionSource<T> completion)
        {
            try { completion.TrySetResult(await operation()); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { Active--; Pump(); }
        }

        internal async UniTask WaitForInstance(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_frame != Time.frameCount) { _frame = Time.frameCount; _created = 0; }
                if (_created < _options.InstancesPerFrame) { _created++; return; }
                await UniTask.NextFrame(PlayerLoopTiming.Update, token, cancelImmediately: true);
            }
        }
    }
}
