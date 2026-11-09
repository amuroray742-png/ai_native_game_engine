using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    /// <summary>Main-thread local assets. Retain a lease for as long as its Unity object is used.</summary>
    public interface IAssets
    {
        AssetScope CreateScope(string name);
        UniTask<AssetLease<T>> LoadAsync<T>(string address, CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        UniTask<AssetLease<T>> LoadSubAssetAsync<T>(string address, string name, CancellationToken cancellationToken = default) where T : UnityEngine.Object;
        UniTask<AssetInstance> InstantiateAsync(string address, Transform parent = null, CancellationToken cancellationToken = default);
        AssetPool CreatePool(string address, AssetPoolOptions options);
        UniTask<AssetScene> LoadSceneAsync(string address, CancellationToken cancellationToken = default);
        AssetDiagnostics GetDiagnostics();
    }

    public sealed class AssetLease<T> : IDisposable where T : UnityEngine.Object
    {
        private T _value;
        internal Action Released;
        private Action _release;
        internal AssetLease(T value, Action release) { _value = value; _release = release; }
        public T Value => _release != null ? _value : throw new ObjectDisposedException(nameof(AssetLease<T>));
        public void Dispose()
        {
            Action release = _release;
            _release = null;
            _value = null;
            release?.Invoke();
            if (release != null) { Released?.Invoke(); Released = null; }
        }
    }
}
