using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetPoolOptions
    {
        public int MaxInactive { get; }
        public AssetPoolOptions(int maxInactive)
        {
            if (maxInactive < 0) { throw new ArgumentOutOfRangeException(nameof(maxInactive)); }
            MaxInactive = maxInactive;
        }
    }
    public interface IPooledInstanceCallbacks
    {
        void OnRent();
        void OnReturn();
    }
    public sealed class PooledAsset : IDisposable
    {
        private AssetPool _pool;
        private readonly AssetInstance _instance;
        internal long DiagnosticId;
        internal PooledAsset(AssetPool pool, AssetInstance instance) { _pool = pool; _instance = instance; }
        internal void Invalidate() { _pool = null; }
        public GameObject Value => _pool != null ? _instance.Value : throw new ObjectDisposedException(nameof(PooledAsset));
        public void Dispose()
        {
            var pool = _pool;
            _pool = null;
            pool?.Return(this, _instance);
        }
    }
    public sealed class AssetPool
    {
        private readonly AssetScope _scope;
        private readonly long _id;
        private readonly string _address;
        private readonly int _maxInactive;
        private readonly GameObject _root;
        private readonly List<AssetInstance> _inactive = new List<AssetInstance>();
        private readonly Dictionary<AssetInstance, PooledAsset> _active = new Dictionary<AssetInstance, PooledAsset>();
        private readonly Dictionary<AssetInstance, (Vector3 position, Quaternion rotation, Vector3 scale)> _transforms = new Dictionary<AssetInstance, (Vector3, Quaternion, Vector3)>();
        private UniTaskCompletionSource _closed;
        internal Action Released;
        public int ActiveCount => _active.Count;
        public int InactiveCount => _inactive.Count;
        internal string Describe() => $"{_address}: active={ActiveCount}, inactive={InactiveCount}, maxInactive={_maxInactive}, closing={_closed != null}";
        internal AssetPool(AssetScope scope, string address, AssetPoolOptions options)
        {
            _scope = scope;
            _id = scope.Runtime.Ownership.Add("pool", address, scope.Id);
            _address = address;
            _maxInactive = (options ?? throw new ArgumentNullException(nameof(options))).MaxInactive;
            _root = new GameObject("AssetPool:" + address) { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.SetActive(false);
        }
        private async UniTask<AssetInstance> CreateAsync(CancellationToken token)
        {
            var instance = await _scope.InstantiateAsync(_address, _root.transform, token);
            _scope.Runtime.Ownership.Change(instance.DiagnosticId, _id, "inactive");
            var transform = instance.Value.transform;
            _transforms.Add(instance, (transform.localPosition, transform.localRotation, transform.localScale));
            instance.Released += () => OnDestroyed(instance);
            instance.Value.SetActive(false);
            return instance;
        }
        public async UniTask<PooledAsset> RentAsync(Transform parent = null, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            cancellationToken.ThrowIfCancellationRequested();
            AssetInstance instance = null;
            while (_inactive.Count != 0 && instance == null)
            {
                var candidate = _inactive[_inactive.Count - 1];
                _inactive.RemoveAt(_inactive.Count - 1);
                if (candidate.IsAlive) { instance = candidate; }
                else { _transforms.Remove(candidate); }
            }
            if (instance == null) { instance = await CreateAsync(cancellationToken); }
            try
            {
                EnsureOpen();
                cancellationToken.ThrowIfCancellationRequested();
                instance.Value.transform.SetParent(parent, false);
                ResetTransform(instance);
                foreach (var callback in instance.Value.GetComponentsInChildren<IPooledInstanceCallbacks>(true)) { callback.OnRent(); }
                EnsureOpen();
                cancellationToken.ThrowIfCancellationRequested();
                instance.Value.SetActive(true);
                EnsureOpen();
                cancellationToken.ThrowIfCancellationRequested();
                if (!instance.IsAlive) { throw new AssetException("InstanceDestroyed", "Pool instance destroyed during activation.", _address, stage: "rent"); }
                var rental = new PooledAsset(this, instance);
                rental.DiagnosticId = _scope.Runtime.Ownership.Add("rental", _address, _id);
                _scope.Runtime.Ownership.Change(instance.DiagnosticId, rental.DiagnosticId, "borrowed");
                _active.Add(instance, rental);
                return rental;
            }
            catch (Exception error) { _scope.Runtime.RecordError(error); instance.Dispose(); _transforms.Remove(instance); throw; }
        }
        internal void Return(PooledAsset rental, AssetInstance instance)
        {
            if (!_active.TryGetValue(instance, out var current) || !ReferenceEquals(current, rental)) { return; }
            _active.Remove(instance);
            _scope.Runtime.Ownership.Remove(rental.DiagnosticId);
            _scope.Runtime.Ownership.Change(instance.DiagnosticId, _id, "inactive");
            if (_closed != null || !instance.IsAlive) { instance.Dispose(); _transforms.Remove(instance); return; }
            try
            {
                foreach (var callback in instance.Value.GetComponentsInChildren<IPooledInstanceCallbacks>(true)) { callback.OnReturn(); }
                if (_closed != null || !instance.IsAlive) { instance.Dispose(); _transforms.Remove(instance); return; }
                instance.Value.SetActive(false);
                if (_closed != null || !instance.IsAlive) { instance.Dispose(); _transforms.Remove(instance); return; }
                instance.Value.transform.SetParent(_root.transform, false);
                if (_closed != null || !instance.IsAlive) { instance.Dispose(); _transforms.Remove(instance); return; }
                ResetTransform(instance);
                if (_inactive.Count < _maxInactive) { _inactive.Add(instance); }
                else { instance.Dispose(); _transforms.Remove(instance); }
            }
            catch (Exception error) { _scope.Runtime.RecordError(error); instance.Dispose(); _transforms.Remove(instance); throw; }
        }
        private void ResetTransform(AssetInstance instance)
        {
            var initial = _transforms[instance];
            var transform = instance.Value.transform;
            transform.localPosition = initial.position;
            transform.localRotation = initial.rotation;
            transform.localScale = initial.scale;
        }
        public async UniTask PrewarmAsync(int count, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 0 || count > _maxInactive) { throw new ArgumentOutOfRangeException(nameof(count)); }
            while (_inactive.Count < count)
            {
                var instance = await CreateAsync(cancellationToken);
                if (_closed != null || cancellationToken.IsCancellationRequested) { instance.Dispose(); cancellationToken.ThrowIfCancellationRequested(); EnsureOpen(); }
                if (_inactive.Count < count) { _inactive.Add(instance); }
                else { instance.Dispose(); _transforms.Remove(instance); }
            }
        }
        public UniTask CloseAsync()
        {
            if (_closed != null && _closed.Task.Status != UniTaskStatus.Faulted) { return _closed.Task; }
            var completion = new UniTaskCompletionSource();
            _closed = completion;
            CloseOperationAsync(completion).Forget();
            return completion.Task;
        }
        private async UniTask CloseOperationAsync(UniTaskCompletionSource completion)
        {
            try
            {
                foreach (var rental in new List<PooledAsset>(_active.Values)) { rental.Dispose(); }
                _inactive.Clear();
                await _scope.CloseAsync();
                _transforms.Clear();
                UnityEngine.Object.Destroy(_root);
                await UniTask.WaitUntil(() => _root == null, PlayerLoopTiming.LastPostLateUpdate);
                _scope.Runtime.Ownership.Remove(_id);
                Released?.Invoke();
                Released = null;
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        private void EnsureOpen()
        {
            _scope.EnsureOpen();
            if (_closed != null) { throw new ObjectDisposedException(nameof(AssetPool)); }
        }
        private void OnDestroyed(AssetInstance instance)
        {
            if (_active.TryGetValue(instance, out var rental))
            { _scope.Runtime.Ownership.Remove(rental.DiagnosticId); rental.Invalidate(); _active.Remove(instance); }
            _inactive.Remove(instance);
            _transforms.Remove(instance);
        }
        internal void ForceRelease()
        {
            _scope.ForceRelease();
            _scope.Runtime.Ownership.Remove(_id);
            _active.Clear();
            _inactive.Clear();
            _transforms.Clear();
            if (_root != null) { UnityEngine.Object.DestroyImmediate(_root); }
            Released?.Invoke();
            Released = null;
        }
    }
}
