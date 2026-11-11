using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    /// <summary>Main-thread local assets. Retain a lease for as long as its Unity object is used.</summary>
    public interface IAssets
    {
        AssetScope CreateScope(string name);
        UniTask<AssetBatch> PreloadAsync(IReadOnlyList<AssetLoadRequest> requests, AssetLoadOptions options = null, IProgress<AssetLoadProgress> progress = null, CancellationToken cancellationToken = default);
        UniTask<AssetBatch> WarmupShadersAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default);
        UniTask<AssetLease<T>> LoadWithOptionsAsync<T>(string address, AssetLoadOptions options, CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        UniTask<AssetLease<T>> LoadSubAssetWithOptionsAsync<T>(string address, string name, AssetLoadOptions options, CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        UniTask<AssetLease<T>> LoadAsync<T>(string address, CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        UniTask<AssetLease<T>> LoadSubAssetAsync<T>(string address, string name, CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        UniTask<AssetInstance> InstantiateAsync(string address, Transform parent = null, CancellationToken cancellationToken = default);
        AssetPool CreatePool(string address, AssetPoolOptions options);
        UniTask<AssetScene> LoadSceneAsync(string address, CancellationToken cancellationToken = default);
        AssetDiagnostics GetDiagnostics();
        void RefreshDiagnosticsMemory();
    }

    public sealed class AssetLease<T> : IDisposable where T : UnityEngine.Object
    {
        private T _value;
        internal Action Released;
        internal long DiagnosticId;
        internal Action OwnershipReleased;
        private Action _release;
        internal AssetLease(T value, Action release) { _value = value; _release = release; }
        public T Value => _release != null ? _value : throw new ObjectDisposedException(nameof(AssetLease<T>));
        public void Dispose()
        {
            Action release = _release;
            _release = null;
            _value = null;
            release?.Invoke();
            if (release != null) { OwnershipReleased?.Invoke(); OwnershipReleased = null; }
            if (release != null) { Released?.Invoke(); Released = null; }
        }
    }
}
