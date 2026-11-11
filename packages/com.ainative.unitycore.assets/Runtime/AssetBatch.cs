using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetLoadRequest
    {
        public string Address { get; }
        public Type Type { get; }
        public string SubAsset { get; }
        public AssetLoadRequest(string address, Type type, string subAsset = null)
        {
            if (string.IsNullOrWhiteSpace(address)) { throw new ArgumentException("An address is required.", nameof(address)); }
            if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type)) { throw new ArgumentException("A Unity object type is required.", nameof(type)); }
            Address = address; Type = type; SubAsset = subAsset;
        }
    }

    public enum AssetLoadStage { Queued, Loading, Draining, Succeeded, Failed, Cancelled }

    public sealed class AssetLoadProgress
    {
        public int Total { get; }
        public int Completed { get; }
        public int Failed { get; }
        public AssetLoadStage Stage { get; }
        internal AssetLoadProgress(int total, int completed, int failed, AssetLoadStage stage)
        { Total = total; Completed = completed; Failed = failed; Stage = stage; }
    }

    public sealed class AssetBatch : IDisposable
    {
        private readonly AssetScope _scope;
        private readonly AssetLease<UnityEngine.Object>[] _leases;
        public int Count => _leases.Length;
        internal AssetBatch(AssetScope scope, AssetLease<UnityEngine.Object>[] leases) { _scope = scope; _leases = leases; }
        public T Get<T>(int index) where T : UnityEngine.Object
        {
            _scope.EnsureOpen();
            return (T)_leases[index].Value;
        }
        public UniTask CloseAsync() => _scope.CloseAsync();
        public void Dispose() { CloseAsync().Forget(_scope.Runtime.RecordError); }

        internal static async UniTask<AssetBatch> Load(AssetScope parent, IReadOnlyList<AssetLoadRequest> requests,
            AssetLoadOptions options, IProgress<AssetLoadProgress> progress, CancellationToken token)
        {
            if (requests == null) { throw new ArgumentNullException(nameof(requests)); }
            var input = requests.ToArray();
            if (input.Any(x => x == null)) { throw new ArgumentException("Requests cannot contain null.", nameof(requests)); }
            token.ThrowIfCancellationRequested();
            var scope = parent.CreateScope("preload");
            var leases = new AssetLease<UnityEngine.Object>[input.Length];
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Exception failure = null;
            int completed = 0;
            int failed = 0;
            void Report(AssetLoadStage stage)
            {
                try { progress?.Report(new AssetLoadProgress(input.Length, completed, failed, stage)); }
                catch (Exception error) { parent.Runtime.RecordError(new InvalidOperationException("Asset progress notification failed.", error)); }
            }
            async UniTask LoadOne(int index)
            {
                try
                {
                    var request = input[index];
                    leases[index] = await scope.LoadOwnedAsync<UnityEngine.Object>(request.Address, request.SubAsset,
                        options, cancellation.Token, request.Type);
                    completed++;
                    Report(AssetLoadStage.Loading);
                }
                catch (Exception error)
                {
                    if (!(error is OperationCanceledException)) { failed++; }
                    if (failure == null) { failure = error; cancellation.Cancel(); }
                }
            }
            Report(AssetLoadStage.Queued);
            Report(AssetLoadStage.Loading);
            await UniTask.WhenAll(Enumerable.Range(0, input.Length).Select(LoadOne));
            try { scope.EnsureOpen(); token.ThrowIfCancellationRequested(); }
            catch (Exception error) { if (failure == null) { failure = error; } }
            if (failure != null)
            {
                Report(AssetLoadStage.Draining);
                try { await scope.CloseAsync(); }
                catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
                Report(failure is OperationCanceledException ? AssetLoadStage.Cancelled : AssetLoadStage.Failed);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            // Results commit before the terminal notification. A consumer may dispose its owner
            // from that notification, just as it may dispose any successfully returned lease.
            Report(AssetLoadStage.Succeeded);
            return new AssetBatch(scope, leases);
        }
    }
}
