using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    internal sealed class AssetRuntime
    {
        internal sealed class BundlePin
        {
            internal BundleEntry Entry;
            internal int References;
            internal bool Settled;
            internal AssetBundle Bundle;
            internal readonly List<BundlePin> Dependencies = new List<BundlePin>();
            internal readonly UniTaskCompletionSource<AssetBundle> Completion = new UniTaskCompletionSource<AssetBundle>();
        }
        private sealed class Resource
        {
            internal int References;
            internal bool Settled;
            internal BundlePin Pin;
            internal UnityEngine.Object Value;
            internal readonly UniTaskCompletionSource<UnityEngine.Object> Completion = new UniTaskCompletionSource<UnityEngine.Object>();
            internal readonly UniTaskCompletionSource Finished = new UniTaskCompletionSource();
        }
        internal readonly IAssetBackend Backend;
        internal readonly List<AssetScope> Scopes = new List<AssetScope>();
        internal readonly HashSet<string> Scenes = new HashSet<string>();
        internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        internal bool Closed;
        internal int Pending;
        private readonly List<string> _errors = new List<string>();
        internal void RecordError(Exception error)
        {
            if (error is OperationCanceledException) { return; }
            if (_errors.Count == 32) { _errors.RemoveAt(0); }
            _errors.Add(error.ToString());
        }
        private readonly Dictionary<string, AssetEntry> _assets;
        private readonly Dictionary<string, BundleEntry> _definitions;
        private readonly Dictionary<string, BundlePin> _bundles = new Dictionary<string, BundlePin>();
        private readonly Dictionary<(string, Type, string), Resource> _resources = new Dictionary<(string, Type, string), Resource>();

        internal AssetRuntime(AssetCatalog catalog, IAssetBackend backend)
        {
            catalog.Validate();
            Backend = backend;
            _assets = catalog.Assets.ToDictionary(x => x.Address, StringComparer.Ordinal);
            _definitions = catalog.Bundles.ToDictionary(x => x.Name, StringComparer.Ordinal);
        }
        internal AssetEntry Find(string address, bool scene)
        {
            if (Closed) { throw new ObjectDisposedException(nameof(AssetsModule)); }
            if (string.IsNullOrEmpty(address) || !_assets.TryGetValue(address, out var entry)) { throw new AssetException("AddressNotFound", "Unknown address.", address, stage: "lookup"); }
            if (entry.IsScene != scene) { throw new AssetException("AssetKindMismatch", "Scene and asset operations are distinct.", address, entry.Bundle, "lookup"); }
            return entry;
        }
        internal BundlePin Pin(string name)
        {
            if (_bundles.TryGetValue(name, out var existing)) { existing.References++; return existing; }
            var pin = new BundlePin { Entry = _definitions[name], References = 1 };
            _bundles.Add(name, pin);
            LoadBundleAsync(pin).Forget();
            return pin;
        }
        private async UniTask LoadBundleAsync(BundlePin pin)
        {
            Pending++;
            try
            {
                foreach (string dependency in pin.Entry.Dependencies)
                {
                    var child = Pin(dependency);
                    pin.Dependencies.Add(child);
                    await child.Completion.Task;
                }
                pin.Bundle = await Backend.LoadBundleAsync(pin.Entry);
                pin.Settled = true;
                pin.Completion.TrySetResult(pin.Bundle);
            }
            catch (Exception error) { RecordError(error); pin.Settled = true; pin.Completion.TrySetException(error); }
            finally { Pending--; Collect(pin); }
        }
        internal void Release(BundlePin pin) { pin.References--; Collect(pin); }
        private void Collect(BundlePin pin)
        {
            if (!pin.Settled || pin.References != 0 || !_bundles.TryGetValue(pin.Entry.Name, out var current) || !ReferenceEquals(current, pin)) { return; }
            _bundles.Remove(pin.Entry.Name);
            Backend.Unload(pin.Bundle);
            foreach (var dependency in pin.Dependencies) { Release(dependency); }
        }
        internal async UniTask<AssetLease<T>> LoadAsync<T>(string address, string subAsset, CancellationToken token, Action<UniTask> track) where T : UnityEngine.Object
        {
            var entry = Find(address, false);
            token.ThrowIfCancellationRequested();
            var key = (address, typeof(T), subAsset);
            if (!_resources.TryGetValue(key, out var resource))
            {
                resource = new Resource { References = 1 };
                _resources.Add(key, resource);
                LoadResourceAsync(key, resource, entry).Forget();
            }
            else { resource.References++; }
            track(resource.Finished.Task);
            try
            {
                var value = await resource.Completion.Task.AttachExternalCancellation(token);
                token.ThrowIfCancellationRequested();
                if (Closed) { throw new OperationCanceledException(Lifetime.Token); }
                return new AssetLease<T>((T)value, () => ReleaseResource(key, resource));
            }
            catch { ReleaseResource(key, resource); throw; }
        }
        private async UniTask LoadResourceAsync((string address, Type type, string subAsset) key, Resource resource, AssetEntry entry)
        {
            Pending++;
            try
            {
                resource.Pin = Pin(entry.Bundle);
                var bundle = await resource.Pin.Completion.Task;
                var value = await Backend.LoadAsync(bundle, entry, key.type, key.subAsset);
                if (value == null || !key.type.IsInstanceOfType(value))
                {
                    throw new AssetException("AssetTypeMismatch", "Asset missing or type differs.", entry.Address, entry.Bundle, "asset");
                }
                resource.Settled = true;
                resource.Value = value;
                resource.Completion.TrySetResult(value);
            }
            catch (Exception error)
            {
                var failure = AssetException.WithContext(error, entry.Address, entry.Bundle, "asset");
                RecordError(failure);
                resource.Settled = true;
                resource.Completion.TrySetException(failure);
            }
            finally { Pending--; CollectResource(key, resource); resource.Finished.TrySetResult(); }
        }
        private void ReleaseResource((string, Type, string) key, Resource resource)
        {
            resource.References--;
            CollectResource(key, resource);
        }
        private void CollectResource((string, Type, string) key, Resource resource)
        {
            if (!resource.Settled || resource.References != 0 || !_resources.TryGetValue(key, out var current) || !ReferenceEquals(current, resource)) { return; }
            _resources.Remove(key);
            if (resource.Pin != null) { Release(resource.Pin); }
        }
        internal AssetDiagnostics Diagnostics() => new AssetDiagnostics(
            _bundles.Values.Select(x => $"{x.Entry.Name}: refs={x.References}, loaded={x.Settled}, fileBytes={x.Entry.Size}, dependencies={string.Join(",", x.Entry.Dependencies)}").ToList(),
            _resources.Select(x => $"{x.Key.Item1}: type={x.Key.Item2.Name}, subAsset={x.Key.Item3}, bundle={x.Value.Pin?.Entry.Name}, refs={x.Value.References}, settled={x.Value.Settled}, objectMemoryBytes={(x.Value.Value != null ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(x.Value.Value) : 0)}").ToList(),
            Scopes.Select(x => x.Describe()).ToList(), Pending,
            Scopes.SelectMany(x => x.DescribePools()).ToList(), new List<string>(_errors));
    }
}
