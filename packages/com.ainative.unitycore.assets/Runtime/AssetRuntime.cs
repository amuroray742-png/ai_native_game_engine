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
            internal long Id;
            internal readonly List<Func<AssetPriority>> Demands = new List<Func<AssetPriority>>();
            internal AssetPriority Priority => Demands.Count == 0 ? (AssetPriority)(-1) : Demands.Max(x => x?.Invoke() ?? AssetPriority.Normal);
            internal Func<AssetPriority> ChildPriority;
            internal readonly List<BundlePin> Dependencies = new List<BundlePin>();
            internal readonly UniTaskCompletionSource<AssetBundle> Completion = new UniTaskCompletionSource<AssetBundle>();
            internal readonly UniTaskCompletionSource Retired = new UniTaskCompletionSource();
        }
        private sealed class Resource
        {
            internal int References;
            internal bool Settled;
            internal BundlePin Pin;
            internal UnityEngine.Object Value;
            internal long Id;
            internal long MemoryBytes;
            internal readonly List<AssetPriority> Priorities = new List<AssetPriority>();
            internal Func<AssetPriority> Priority;
            internal readonly UniTaskCompletionSource<UnityEngine.Object> Completion = new UniTaskCompletionSource<UnityEngine.Object>();
            internal readonly UniTaskCompletionSource Finished = new UniTaskCompletionSource();
        }
        internal readonly IAssetBackend Backend;
        internal readonly AssetLoadScheduler Scheduler;
        internal readonly AssetOwnershipTracker Ownership = new AssetOwnershipTracker();
        internal static string ResourceKey(string address, Type type, string subAsset)
            => address + "|" + type.FullName + "|" + subAsset;
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

        internal AssetRuntime(AssetCatalog catalog, IAssetBackend backend, AssetRuntimeOptions options = null)
        {
            catalog.Validate();
            Backend = backend;
            Scheduler = new AssetLoadScheduler(options ?? new AssetRuntimeOptions());
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
        internal async UniTask WaitForRetiredBundles(string name, CancellationToken token)
        {
            var names = new HashSet<string>();
            void Visit(string current)
            {
                if (!names.Add(current)) { return; }
                foreach (string child in _definitions[current].Dependencies) { Visit(child); }
            }
            Visit(name);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var retiring = names.Select(x => _bundles.TryGetValue(x, out var pin) ? pin : null)
                    .FirstOrDefault(x => x != null && x.Priority < AssetPriority.Low);
                if (retiring == null) { return; }
                await retiring.Retired.Task.AttachExternalCancellation(token);
            }
        }
        internal BundlePin Pin(string name, Func<AssetPriority> priority = null)
        {
            if (_bundles.TryGetValue(name, out var existing)) { existing.References++; existing.Demands.Add(priority); return existing; }
            var pin = new BundlePin { Entry = _definitions[name], References = 1 };
            pin.Demands.Add(priority);
            pin.Id = Ownership.Add("bundle", name, bundle: name, state: "loading", dependencies: pin.Entry.Dependencies);
            pin.ChildPriority = () => pin.Priority;
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
                    var child = Pin(dependency, pin.ChildPriority);
                    pin.Dependencies.Add(child);
                }
                Exception dependencyFailure = null;
                foreach (var child in pin.Dependencies)
                {
                    try { await child.Completion.Task; }
                    catch (Exception error) { if (dependencyFailure == null) { dependencyFailure = error; } }
                }
                if (dependencyFailure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(dependencyFailure).Throw(); }
                pin.Bundle = await RunOperation("bundle:" + pin.Entry.Name, pin.Id, () => Backend.LoadBundleAsync(pin.Entry), () => pin.Priority,
                    () => pin.References > 0 && pin.Priority >= AssetPriority.Low && !Closed);
                Ownership.Change(pin.Id, state: "loaded");
                pin.Settled = true;
                pin.Completion.TrySetResult(pin.Bundle);
            }
            catch (Exception error) { RecordError(error); pin.Settled = true; pin.Completion.TrySetException(error); }
            finally { Pending--; Collect(pin); }
        }
        internal void Release(BundlePin pin, Func<AssetPriority> priority = null)
        {
            pin.References--;
            pin.Demands.Remove(priority);
            Scheduler.Pump();
            Collect(pin);
        }
        private void Collect(BundlePin pin)
        {
            if (!pin.Settled || pin.References != 0 || !_bundles.TryGetValue(pin.Entry.Name, out var current) || !ReferenceEquals(current, pin)) { return; }
            _bundles.Remove(pin.Entry.Name);
            Ownership.Remove(pin.Id);
            Backend.Unload(pin.Bundle);
            foreach (var dependency in pin.Dependencies) { Release(dependency, pin.ChildPriority); }
            pin.Retired.TrySetResult();
        }
        internal async UniTask<AssetLease<T>> LoadAsync<T>(string address, string subAsset, CancellationToken token, Action<UniTask> track,
            AssetPriority priority = AssetPriority.Normal, Type requestedType = null) where T : UnityEngine.Object
        {
            var entry = Find(address, false);
            token.ThrowIfCancellationRequested();
            var key = (address, requestedType ?? typeof(T), subAsset);
            while (true)
            {
                if (_resources.TryGetValue(key, out var retiring) && retiring.References == 0)
                { await retiring.Finished.Task.AttachExternalCancellation(token); continue; }
                await WaitForRetiredBundles(entry.Bundle, token);
                if (!_resources.TryGetValue(key, out retiring) || retiring.References != 0) { break; }
            }
            if (!_resources.TryGetValue(key, out var resource))
            {
                resource = new Resource { References = 1 };
                resource.Id = Ownership.Add("resource", address, resource: ResourceKey(address, key.Item2, subAsset), bundle: entry.Bundle, state: "loading");
                resource.Priorities.Add(priority);
                var captured = resource;
                resource.Priority = () => captured.Priorities.Count == 0 ? (AssetPriority)(-1) : captured.Priorities.Max();
                _resources.Add(key, resource);
                LoadResourceAsync(key, resource, entry).Forget();
            }
            else { resource.References++; resource.Priorities.Add(priority); Ownership.SharedHits++; }
            track(resource.Finished.Task);
            try
            {
                var value = await resource.Completion.Task.AttachExternalCancellation(token);
                token.ThrowIfCancellationRequested();
                if (Closed) { throw new OperationCanceledException(Lifetime.Token); }
                return new AssetLease<T>((T)value, () => ReleaseResource(key, resource, priority));
            }
            catch { ReleaseResource(key, resource, priority); throw; }
        }
        private async UniTask LoadResourceAsync((string address, Type type, string subAsset) key, Resource resource, AssetEntry entry)
        {
            Pending++;
            try
            {
                resource.Pin = Pin(entry.Bundle, resource.Priority);
                var bundle = await resource.Pin.Completion.Task;
                var value = await RunOperation("asset:" + entry.Address, resource.Id, () => Backend.LoadAsync(bundle, entry, key.type, key.subAsset), resource.Priority,
                    () => resource.References > 0 && !Closed);
                if (value == null || !key.type.IsInstanceOfType(value))
                {
                    throw new AssetException("AssetTypeMismatch", "Asset missing or type differs.", entry.Address, entry.Bundle, "asset");
                }
                resource.Settled = true;
                resource.Value = value;
                Ownership.Change(resource.Id, state: "loaded");
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
        private void ReleaseResource((string, Type, string) key, Resource resource, AssetPriority priority)
        {
            resource.References--;
            resource.Priorities.Remove(priority);
            Scheduler.Pump();
            CollectResource(key, resource);
        }
        private void CollectResource((string, Type, string) key, Resource resource)
        {
            if (!resource.Settled || resource.References != 0 || !_resources.TryGetValue(key, out var current) || !ReferenceEquals(current, resource)) { return; }
            _resources.Remove(key);
            Ownership.Remove(resource.Id);
            if (resource.Pin != null) { Release(resource.Pin, resource.Priority); }
        }
        internal async UniTask<T> RunOperation<T>(string name, long owner, Func<UniTask<T>> operation,
            Func<AssetPriority> priority, Func<bool> wanted)
        {
            long id = Ownership.Add("request", name, owner, state: "queued");
            double queued = AssetOwnershipTracker.Now;
            double started = -1;
            Exception failure = null;
            async UniTask<T> Start()
            {
                started = AssetOwnershipTracker.Now;
                Ownership.Change(id, state: "loading");
                return await operation();
            }
            try { return await Scheduler.Run(Start, priority, wanted); }
            catch (Exception error) { failure = error; throw; }
            finally { Ownership.Finish(id, name, queued, started, failure); }
        }
        internal void RefreshMemory()
        {
            foreach (var resource in _resources.Values)
            { resource.MemoryBytes = resource.Value != null ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(resource.Value) : 0; }
        }
        internal AssetDiagnostics Diagnostics() => new AssetDiagnostics(
            _bundles.Values.Select(x => $"{x.Entry.Name}: refs={x.References}, loaded={x.Settled}, fileBytes={x.Entry.Size}, dependencies={string.Join(",", x.Entry.Dependencies)}").ToList(),
            _resources.Select(x => $"{x.Key.Item1}: type={x.Key.Item2.Name}, subAsset={x.Key.Item3}, bundle={x.Value.Pin?.Entry.Name}, refs={x.Value.References}, settled={x.Value.Settled}, objectMemoryBytes={x.Value.MemoryBytes}").ToList(),
            Scopes.Select(x => x.Describe()).ToList(), Pending,
            Scopes.SelectMany(x => x.DescribePools()).ToList(), new List<string>(_errors), Ownership.Snapshot(Scheduler));
    }
}
