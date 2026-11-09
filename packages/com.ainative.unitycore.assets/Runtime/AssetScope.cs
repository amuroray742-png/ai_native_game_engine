using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    /// <summary>Explicit owner. CloseAsync cancels pending consumers and drains its instances, scenes and pools.</summary>
    public sealed class AssetScope : IAssets
    {
        internal readonly AssetRuntime Runtime;
        private readonly string _name;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly List<IDisposable> _leases = new List<IDisposable>();
        private readonly List<AssetScope> _children = new List<AssetScope>();
        private readonly List<AssetInstance> _instances = new List<AssetInstance>();
        private readonly List<AssetPool> _pools = new List<AssetPool>();
        private readonly List<AssetScene> _scenes = new List<AssetScene>();
        private UniTaskCompletionSource _closed;
        private int _pending;
        private int _nativePending;
        private bool _forced;
        internal Action Released;
        internal AssetScope(AssetRuntime runtime, string name)
        {
            Runtime = runtime;
            _name = name;
            Runtime.Scopes.Add(this);
        }
        internal void EnsureOpen()
        {
            if (_closed != null || _forced || Runtime.Closed) { throw new ObjectDisposedException(_name); }
        }
        public AssetScope CreateScope(string name)
        {
            EnsureOpen();
            var child = new AssetScope(Runtime, name);
            _children.Add(child);
            child.Released = () => { _children.Remove(child); TryUnregister(); };
            return child;
        }
        public UniTask<AssetLease<T>> LoadAsync<T>(string address, CancellationToken cancellationToken = default) where T : UnityEngine.Object
            => LoadSubAssetAsync<T>(address, null, cancellationToken);
        public async UniTask<AssetLease<T>> LoadSubAssetAsync<T>(string address, string name, CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            EnsureOpen();
            _pending++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token, Runtime.Lifetime.Token);
            try
            {
                var lease = await Runtime.LoadAsync<T>(address, name, linked.Token, Track);
                _leases.Add(lease);
                lease.Released = () => { _leases.Remove(lease); TryUnregister(); };
                return lease;
            }
            finally { _pending--; TryUnregister(); }
        }
        private void Track(UniTask operation)
        {
            _nativePending++;
            TrackAsync(operation).Forget(Debug.LogException);
        }
        private async UniTask TrackAsync(UniTask operation)
        {
            try { await operation; }
            finally { _nativePending--; TryUnregister(); }
        }
        public async UniTask<AssetInstance> InstantiateAsync(string address, Transform parent = null, CancellationToken cancellationToken = default)
        {
            var lease = await LoadAsync<GameObject>(address, cancellationToken);
            AssetInstance instance = null;
            _pending++;
            _leases.Remove(lease);
            lease.Released = null;
            try
            {
                EnsureOpen();
                cancellationToken.ThrowIfCancellationRequested();
                instance = AssetInstance.Create(lease, parent);
                EnsureOpen();
                cancellationToken.ThrowIfCancellationRequested();
                _instances.Add(instance);
                instance.Released = () => { _instances.Remove(instance); TryUnregister(); };
                return instance;
            }
            catch
            {
                if (instance == null) { lease.Dispose(); }
                else if (_forced) { instance.ForceDestroy(); }
                else { instance.Dispose(); await instance.Destroyed; }
                throw;
            }
            finally { _pending--; TryUnregister(); }
        }
        public AssetPool CreatePool(string address, AssetPoolOptions options)
        {
            EnsureOpen();
            if (options == null) { throw new ArgumentNullException(nameof(options)); }
            Runtime.Find(address, false);
            var pool = new AssetPool(CreateScope("pool:" + address), address, options);
            _pools.Add(pool);
            pool.Released = () => { _pools.Remove(pool); TryUnregister(); };
            return pool;
        }
        public async UniTask<AssetScene> LoadSceneAsync(string address, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Runtime.Find(address, true);
            if (!Runtime.Scenes.Add(entry.Path)) { throw new AssetException("SceneAlreadyLoaded", "Scene already requested.", address); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token, Runtime.Lifetime.Token);
            var operation = LoadSceneOwnedAsync(entry, linked.Token);
            return await operation.AttachExternalCancellation(linked.Token);
        }
        private async UniTask<AssetScene> LoadSceneOwnedAsync(AssetEntry entry, CancellationToken token)
        {
            Runtime.Pending++;
            _pending++;
            var pin = Runtime.Pin(entry.Bundle);
            bool transferred = false;
            try
            {
                await pin.Completion.Task;
                token.ThrowIfCancellationRequested();
                var scene = await Runtime.Backend.LoadSceneAsync(entry);
                var handle = new AssetScene(Runtime, entry, pin, scene);
                transferred = true;
                _scenes.Add(handle);
                handle.Released = () => { _scenes.Remove(handle); TryUnregister(); };
                if (token.IsCancellationRequested || Runtime.Closed)
                {
                    await handle.UnloadAsync();
                    throw new OperationCanceledException(token);
                }
                return handle;
            }
            catch (Exception error)
            {
                var failure = AssetException.WithContext(error, entry.Address, entry.Bundle, "scene");
                Runtime.RecordError(failure);
                throw failure;
            }
            finally
            {
                if (!transferred) { Runtime.Release(pin); Runtime.Scenes.Remove(entry.Path); }
                Runtime.Pending--;
                _pending--;
                TryUnregister();
            }
        }
        public AssetDiagnostics GetDiagnostics() => Runtime.Diagnostics();
        internal string Describe() => $"{_name}: pending={_pending}, leases={_leases.Count}, instances={_instances.FindAll(x => x.IsAlive).Count}, pools={_pools.Count}, scenes={_scenes.FindAll(x => x.IsLoaded).Count}";
        internal IEnumerable<string> DescribePools()
        {
            foreach (var pool in _pools) { yield return _name + ": " + pool.Describe(); }
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
            var errors = new List<Exception>();
            try { _cancellation.Cancel(); } catch (Exception error) { errors.Add(error); }
            foreach (var pool in _pools.ToArray())
            {
                try { await pool.CloseAsync(); } catch (Exception error) { errors.Add(error); }
            }
            foreach (var child in _children.ToArray())
            {
                try { await child.CloseAsync(); } catch (Exception error) { errors.Add(error); }
            }
            while (_pending != 0 || _nativePending != 0) { await UniTask.Yield(); }
            foreach (var scene in _scenes.ToArray())
            {
                try { await scene.UnloadAsync(); } catch (Exception error) { errors.Add(error); }
            }
            var instances = _instances.ToArray();
            var destroying = new List<AssetInstance>();
            foreach (var instance in instances)
            {
                try { instance.Dispose(); destroying.Add(instance); } catch (Exception error) { errors.Add(error); }
            }
            foreach (var instance in destroying)
            {
                try { await instance.Destroyed; } catch (Exception error) { errors.Add(error); }
            }
            foreach (var lease in _leases.ToArray())
            {
                try { lease.Dispose(); } catch (Exception error) { errors.Add(error); }
            }
            TryUnregister();
            if (errors.Count == 0) { completion.TrySetResult(); }
            else { completion.TrySetException(new AggregateException(errors)); }
        }
        // A failed close remains owned and visible until the remaining resources actually leave.
        private void TryUnregister()
        {
            if ((_closed == null && !_forced) || _pending != 0 || _nativePending != 0
                || _leases.Count != 0 || _instances.Count != 0 || _scenes.Count != 0
                || _children.Count != 0 || _pools.Count != 0) { return; }
            Runtime.Scopes.Remove(this);
            var released = Released;
            Released = null;
            released?.Invoke();
        }
        internal void ForceRelease()
        {
            _forced = true;
            _cancellation.Cancel();
            foreach (var pool in _pools.ToArray()) { pool.ForceRelease(); }
            foreach (var child in _children.ToArray()) { child.ForceRelease(); }
            foreach (var instance in _instances.ToArray()) { instance.ForceDestroy(); }
            foreach (var scene in _scenes.ToArray()) { scene.UnloadAsync().Forget(Debug.LogException); }
            foreach (var lease in _leases.ToArray()) { lease.Dispose(); }
            TryUnregister();
        }
    }
}
