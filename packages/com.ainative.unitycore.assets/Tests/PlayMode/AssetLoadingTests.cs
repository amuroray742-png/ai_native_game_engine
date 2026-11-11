using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class AssetLoadingTests
    {
        private sealed class Backend : IAssetBackend
        {
            internal readonly Dictionary<string, UniTaskCompletionSource<AssetBundle>> Bundles = new Dictionary<string, UniTaskCompletionSource<AssetBundle>>();
            internal readonly Dictionary<string, UniTaskCompletionSource<UnityEngine.Object>> Assets = new Dictionary<string, UniTaskCompletionSource<UnityEngine.Object>>();
            internal readonly List<string> Started = new List<string>();
            public UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry)
            { Started.Add(entry.Name); return Bundles.TryGetValue(entry.Name, out var gate) ? gate.Task : UniTask.FromResult<AssetBundle>(null); }
            public UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset)
            { Started.Add(entry.Address); return Assets[entry.Address].Task; }
            public void Unload(AssetBundle bundle) { }
            public UniTask<Scene> LoadSceneAsync(AssetEntry entry) => throw new InvalidOperationException("Cancelled scene must never start.");
            public UniTask UnloadSceneAsync(Scene scene) => UniTask.CompletedTask;
        }
        private sealed class Progress : IProgress<AssetLoadProgress>
        {
            internal readonly List<AssetLoadProgress> Values = new List<AssetLoadProgress>();
            internal Action<AssetLoadProgress> OnReport;
            public void Report(AssetLoadProgress value) { Values.Add(value); OnReport?.Invoke(value); }
        }
        private static AssetScope Create(Backend backend, BundleEntry[] bundles = null, int limit = 4)
        {
            var catalog = new AssetCatalog
            {
                UnityVersion = Application.unityVersion,
                Bundles = bundles ?? new[] { new BundleEntry { Name = "bundle" }, new BundleEntry { Name = "scene-bundle" } },
                Assets = new[]
                {
                    new AssetEntry { Address = "a", Path = "a", Bundle = "bundle" },
                    new AssetEntry { Address = "b", Path = "b", Bundle = "bundle" },
                    new AssetEntry { Address = "scene", Path = "scene", Bundle = "scene-bundle", IsScene = true }
                }
            };
            return new AssetScope(new AssetRuntime(catalog, backend, new AssetRuntimeOptions(limit, 1)), "test");
        }

        [UnityTest]
        public IEnumerator BatchRollbackPreservesOtherConsumersAndTracksCancellation() => UniTask.ToCoroutine(async () =>
        {
            var backend = new Backend();
            backend.Assets["a"] = new UniTaskCompletionSource<UnityEngine.Object>();
            backend.Assets["b"] = new UniTaskCompletionSource<UnityEngine.Object>();
            var root = Create(backend);
            var texture = new Texture2D(1, 1);
            var other = root.LoadAsync<Texture2D>("a");
            var progress = new Progress();
            var batch = root.PreloadAsync(new[] { new AssetLoadRequest("a", typeof(Texture2D)), new AssetLoadRequest("b", typeof(Texture2D)) }, progress: progress);
            Assert.AreEqual(3, root.GetDiagnostics().Ownership.Owners.Count(x => x.Kind == "consumer"));
            backend.Assets["b"].TrySetException(new InvalidOperationException("failed b"));
            Assert.AreEqual(UniTaskStatus.Pending, batch.Status, "Rollback must drain shared native work.");
            backend.Assets["a"].TrySetResult(texture);
            Exception failure = null;
            try { await batch; } catch (Exception error) { failure = error; }
            Assert.NotNull(failure);
            var lease = await other;
            Assert.AreSame(texture, lease.Value);
            Assert.AreEqual(AssetLoadStage.Failed, progress.Values.Last().Stage);
            Assert.Greater(root.GetDiagnostics().Ownership.Cancellations, 0);
            Assert.Greater(root.GetDiagnostics().Ownership.Failures, 0);
            Assert.AreEqual(1, backend.Started.Count(x => x == "a"));
            await root.CloseAsync();
            Assert.Zero(root.GetDiagnostics().Ownership.Owners.Count);
            UnityEngine.Object.Destroy(texture);
        });

        [UnityTest]
        public IEnumerator FailedDependencyStillDrainsSibling() => UniTask.ToCoroutine(async () =>
        {
            var backend = new Backend();
            backend.Bundles["left"] = new UniTaskCompletionSource<AssetBundle>();
            backend.Bundles["right"] = new UniTaskCompletionSource<AssetBundle>();
            var root = Create(backend, new[] { new BundleEntry { Name = "bundle", Dependencies = new[] { "left", "right" } },
                new BundleEntry { Name = "left" }, new BundleEntry { Name = "right" }, new BundleEntry { Name = "scene-bundle" } });
            var loading = root.LoadAsync<Texture2D>("a");
            backend.Bundles["left"].TrySetException(new InvalidOperationException("left failed"));
            var closing = root.CloseAsync();
            Assert.AreEqual(UniTaskStatus.Pending, closing.Status);
            backend.Bundles["right"].TrySetResult(null);
            try { await loading; } catch (Exception) { }
            await closing;
            Assert.Zero(root.GetDiagnostics().PendingOperations);
            Assert.Zero(root.GetDiagnostics().Ownership.Owners.Count);
        });

        [UnityTest]
        public IEnumerator CancelledQueuedSceneClosesWithoutWaitingForUnrelatedLoad() => UniTask.ToCoroutine(async () =>
        {
            var backend = new Backend();
            backend.Assets["a"] = new UniTaskCompletionSource<UnityEngine.Object>();
            var root = Create(backend, limit: 1);
            var unrelated = root.LoadAsync<Texture2D>("a");
            var sceneOwner = root.CreateScope("scene owner");
            using var cancellation = new CancellationTokenSource();
            var scene = sceneOwner.LoadSceneAsync("scene", cancellation.Token);
            cancellation.Cancel();
            Assert.True((await scene.SuppressCancellationThrow()).IsCanceled);
            await sceneOwner.CloseAsync();
            Assert.False(backend.Started.Contains("scene-bundle"));
            Assert.AreEqual(UniTaskStatus.Pending, unrelated.Status);
            var texture = new Texture2D(1, 1);
            backend.Assets["a"].TrySetResult(texture);
            (await unrelated).Dispose();
            await root.CloseAsync();
            UnityEngine.Object.Destroy(texture);
        });

        [UnityTest]
        public IEnumerator BatchDuplicatesCommitBeforeSuccessNotificationAndSnapshotsAreIsolated() => UniTask.ToCoroutine(async () =>
        {
            var texture = new Texture2D(1, 1);
            var backend = new Backend();
            backend.Assets["a"] = new UniTaskCompletionSource<UnityEngine.Object>();
            backend.Assets["a"].TrySetResult(texture);
            var root = Create(backend);
            var batch = await root.PreloadAsync(new[] { new AssetLoadRequest("a", typeof(Texture2D)), new AssetLoadRequest("a", typeof(Texture2D)) });
            Assert.AreSame(batch.Get<Texture2D>(0), batch.Get<Texture2D>(1));
            var baseline = root.GetDiagnostics().Ownership;
            await batch.CloseAsync();
            Assert.Throws<ObjectDisposedException>(() => batch.Get<Texture2D>(0));
            Assert.IsTrue(baseline.Owners.Any(x => x.Kind == "lease"));
            Assert.IsFalse(root.GetDiagnostics().Ownership.Owners.Any(x => x.Kind == "lease"));
            for (int i = 0; i < 300; i++) { (await root.LoadAsync<Texture2D>("a", default)).Dispose(); }
            Assert.AreEqual(256, root.GetDiagnostics().Ownership.RecentOperations.Count);
            using var cancellation = new CancellationTokenSource();
            var progress = new Progress { OnReport = value => { if (value.Stage == AssetLoadStage.Succeeded) { cancellation.Cancel(); } } };
            var committed = await root.PreloadAsync(Array.Empty<AssetLoadRequest>(), progress: progress, cancellationToken: cancellation.Token);
            Assert.AreEqual(0, committed.Count);
            await root.CloseAsync();
            UnityEngine.Object.Destroy(texture);
        });

        [UnityTest]
        public IEnumerator InstanceBudgetAndForcedRetirementKeepPrefabOwned() => UniTask.ToCoroutine(async () =>
        {
            var backend = new Backend();
            var prefab = new GameObject("budget source");
            backend.Assets["a"] = new UniTaskCompletionSource<UnityEngine.Object>();
            backend.Assets["a"].TrySetResult(prefab);
            var root = Create(backend);
            var first = await root.InstantiateAsync("a");
            int firstFrame = Time.frameCount;
            var second = root.InstantiateAsync("a");
            Assert.AreEqual(UniTaskStatus.Pending, second.Status);
            var created = await second;
            Assert.Greater(Time.frameCount, firstFrame);
            var third = root.InstantiateAsync("a");
            Assert.AreEqual(UniTaskStatus.Pending, third.Status);
            root.ForceRelease();
            Assert.True((await third.SuppressCancellationThrow()).IsCanceled);
            Assert.Zero(root.GetDiagnostics().Bundles.Count);
            Assert.Zero(root.GetDiagnostics().Ownership.Owners.Count);
            UnityEngine.Object.Destroy(prefab);
        });

        [UnityTest]
        public IEnumerator SynchronousProgressCancellationNeverStartsLaterBatchItems() => UniTask.ToCoroutine(async () =>
        {
            var backend = new Backend();
            var texture = new Texture2D(1, 1);
            backend.Assets["a"] = new UniTaskCompletionSource<UnityEngine.Object>();
            backend.Assets["a"].TrySetResult(texture);
            backend.Assets["b"] = new UniTaskCompletionSource<UnityEngine.Object>();
            var root = Create(backend);
            using var cancellation = new CancellationTokenSource();
            var progress = new Progress { OnReport = p => { if (p.Completed == 1) { cancellation.Cancel(); } } };
            var operation = root.PreloadAsync(new[] { new AssetLoadRequest("a", typeof(Texture2D)), new AssetLoadRequest("b", typeof(Texture2D)) },
                progress: progress, cancellationToken: cancellation.Token);
            Assert.True((await operation.SuppressCancellationThrow()).IsCanceled);
            Assert.False(backend.Started.Contains("b"));
            Assert.AreEqual(AssetLoadStage.Cancelled, progress.Values.Last().Stage);
            await root.CloseAsync();
            Assert.Zero(root.GetDiagnostics().Ownership.Owners.Count);
            UnityEngine.Object.Destroy(texture);
        });

        [UnityTest]
        public IEnumerator MixedSubAssetsAndThrowingProgressKeepTheirResults() => UniTask.ToCoroutine(async () =>
        {
            var backend = new Backend();
            var texture = new Texture2D(2, 2);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero);
            backend.Assets["a"] = new UniTaskCompletionSource<UnityEngine.Object>();
            backend.Assets["b"] = new UniTaskCompletionSource<UnityEngine.Object>();
            backend.Assets["a"].TrySetResult(texture);
            backend.Assets["b"].TrySetResult(sprite);
            var root = Create(backend);
            var progress = new Progress { OnReport = _ => throw new OperationCanceledException("observer only") };
            var batch = await root.PreloadAsync(new[] { new AssetLoadRequest("a", typeof(Texture2D)),
                new AssetLoadRequest("b", typeof(Sprite), "icon"), new AssetLoadRequest("a", typeof(Texture2D)) }, progress: progress);
            Assert.AreSame(texture, batch.Get<Texture2D>(0));
            Assert.AreSame(sprite, batch.Get<Sprite>(1));
            Assert.AreSame(texture, batch.Get<Texture2D>(2));
            Assert.AreEqual(1, backend.Started.Count(x => x == "a"));
            Assert.IsTrue(root.GetDiagnostics().Errors.Any(x => x.Contains("observer only")));
            await root.CloseAsync();
            Assert.Throws<ObjectDisposedException>(() => batch.Get<Texture2D>(0));
            UnityEngine.Object.Destroy(sprite);
            UnityEngine.Object.Destroy(texture);
        });

        [UnityTest]
        public IEnumerator CancelledDependencyGraphCanBeRetriedBeforeItDrains() => UniTask.ToCoroutine(async () =>
        {
            foreach (string address in new[] { "a", "b" })
            {
                var backend = new Backend();
                backend.Bundles["left"] = new UniTaskCompletionSource<AssetBundle>();
                var texture = new Texture2D(1, 1);
                backend.Assets[address] = new UniTaskCompletionSource<UnityEngine.Object>();
                backend.Assets[address].TrySetResult(texture);
                var root = Create(backend, new[] { new BundleEntry { Name = "bundle", Dependencies = new[] { "left", "right" } },
                    new BundleEntry { Name = "left" }, new BundleEntry { Name = "right" }, new BundleEntry { Name = "scene-bundle" } }, 1);
                using var cancellation = new CancellationTokenSource();
                var old = root.LoadAsync<Texture2D>("a", cancellation.Token);
                cancellation.Cancel();
                Assert.True((await old.SuppressCancellationThrow()).IsCanceled);
                var retry = root.LoadAsync<Texture2D>(address);
                Assert.AreEqual(UniTaskStatus.Pending, retry.Status);
                Assert.AreEqual(1, backend.Started.Count(x => x == "left"));
                backend.Bundles["left"].TrySetResult(null);
                using (var lease = await retry) { Assert.AreSame(texture, lease.Value); }
                Assert.AreEqual(1, backend.Started.Count(x => x == "right"));
                await root.CloseAsync();
                Assert.Zero(root.GetDiagnostics().Ownership.Owners.Count);
                UnityEngine.Object.Destroy(texture);
            }
        });

        [UnityTest]
        public IEnumerator WarmupBetweenCollectionsIsCancelledByOwnerClose() => UniTask.ToCoroutine(async () =>
        {
            foreach (bool force in new[] { false, true })
            {
                var backend = new Backend();
                var collection = new ShaderVariantCollection();
                foreach (string address in new[] { "a", "b" })
                {
                    backend.Assets[address] = new UniTaskCompletionSource<UnityEngine.Object>();
                    backend.Assets[address].TrySetResult(collection);
                }
                var root = Create(backend);
                var warming = root.WarmupShadersAsync(new[] { "a", "b" });
                Assert.AreEqual(UniTaskStatus.Pending, warming.Status);
                if (force) { root.ForceRelease(); } else { await root.CloseAsync(); }
                Assert.True((await warming.SuppressCancellationThrow()).IsCanceled);
                Assert.Zero(root.GetDiagnostics().Ownership.Owners.Count);
                UnityEngine.Object.Destroy(collection);
            }
        });

        [UnityTest]
        public IEnumerator SchedulerHonorsBoundFairnessAndSynchronousCancellation() => UniTask.ToCoroutine(async () =>
        {
            var scheduler = new AssetLoadScheduler(new AssetRuntimeOptions(1, 1));
            var blocker = new UniTaskCompletionSource<int>();
            var tasks = new List<UniTask<int>> { scheduler.Run(() => blocker.Task, () => AssetPriority.Normal, () => true) };
            var starts = new List<int>();
            bool wanted = true;
            tasks.Add(scheduler.Run(() => { starts.Add(-1); wanted = false; return UniTask.FromResult(1); }, () => AssetPriority.Low, () => true));
            for (int i = 0; i < 12; i++)
            {
                int item = i;
                tasks.Add(scheduler.Run(() => { starts.Add(item); return UniTask.FromResult(item); }, () => AssetPriority.High, () => wanted));
            }
            blocker.TrySetResult(0);
            foreach (var task in tasks) { await task.SuppressCancellationThrow(); }
            CollectionAssert.AreEqual(Enumerable.Range(0, 8).Concat(new[] { -1 }), starts);
            Assert.AreEqual(1, scheduler.Peak);
            Assert.Zero(scheduler.Active);
            Assert.Zero(scheduler.Queued);
        });
    }
}
