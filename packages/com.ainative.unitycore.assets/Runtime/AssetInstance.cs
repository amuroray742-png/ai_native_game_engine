using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetInstance : IDisposable
    {
        private readonly GameObject _object;
        private readonly InstanceLifetime _lifetime;
        private bool _disposed;
        internal Action Released;
        internal long DiagnosticId;
        private AssetInstance(GameObject value, InstanceLifetime lifetime) { _object = value; _lifetime = lifetime; }
        public bool IsAlive => !_disposed && _object != null;
        public GameObject Value => IsAlive ? _object : throw new ObjectDisposedException(nameof(AssetInstance));
        internal UniTask Destroyed => _lifetime.Completion.Task;
        internal static AssetInstance Create(AssetLease<GameObject> lease, Transform parent)
        {
            var value = UnityEngine.Object.Instantiate(lease.Value, parent, false);
            var lifetime = value.AddComponent<InstanceLifetime>();
            lifetime.Bind(lease);
            var instance = new AssetInstance(value, lifetime);
            instance.ObserveAsync().Forget(Debug.LogException);
            return instance;
        }
        private async UniTask ObserveAsync() { await _lifetime.Completion.Task; Released?.Invoke(); Released = null; }
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            if (_object != null) { UnityEngine.Object.Destroy(_object); }
        }
        internal void ForceDestroy()
        {
            _disposed = true;
            if (_object != null) { UnityEngine.Object.DestroyImmediate(_object); }
            _lifetime.Release();
        }
    }
}
