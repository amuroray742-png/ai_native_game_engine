using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class AssetCancellationTests
    {
        private sealed class ControlledBackend : IAssetBackend
        {
            internal readonly UniTaskCompletionSource<UnityEngine.Object> Asset = new UniTaskCompletionSource<UnityEngine.Object>();
            internal readonly UniTaskCompletionSource SceneGate = new UniTaskCompletionSource();
            internal bool SceneStarted;
            internal Scene Scene;
            internal int Loads;
            internal int Unloads;
            internal bool RejectSceneUnload;
            public async UniTask UnloadSceneAsync(Scene scene)
            {
                if (RejectSceneUnload) { throw new InvalidOperationException("Engine refused scene unload"); }
                await SceneManager.UnloadSceneAsync(scene);
            }
            public UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry) { Loads++; return UniTask.FromResult<AssetBundle>(null); }
            public void Unload(AssetBundle bundle) { Unloads++; }
            public UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset) => Asset.Task;
            public async UniTask<Scene> LoadSceneAsync(AssetEntry entry)
            {
                SceneStarted = true;
                await SceneGate.Task;
                Scene = SceneManager.CreateScene("controlled-" + Guid.NewGuid().ToString("N"));
                return Scene;
            }
        }
        private static AssetScope Scope(ControlledBackend backend)
        {
            var catalog = new AssetCatalog { UnityVersion = Application.unityVersion,
                Bundles = new[] { new BundleEntry { Name = "bundle" } },
                Assets = new[] { new AssetEntry { Address = "asset", Bundle = "bundle", Path = "asset" },
                    new AssetEntry { Address = "scene", Bundle = "bundle", Path = "scene", IsScene = true } } };
            return new AssetScope(new AssetRuntime(catalog, backend), "controlled");
        }
        [UnityTest]
        public IEnumerator AlreadyCancelledSceneDoesNotStartBundleLoads() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.True((await scope.LoadSceneAsync("scene", cancellation.Token).SuppressCancellationThrow()).IsCanceled);
            Assert.Zero(backend.Loads);
            Assert.False(backend.SceneStarted);
            await scope.CloseAsync();
            Assert.Zero(scope.GetDiagnostics().Scopes.Count);
        });
        [UnityTest]
        public IEnumerator FailedCloseRetainsSceneOwnershipAndCanBeRetried() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend { RejectSceneUnload = true };
            backend.SceneGate.TrySetResult();
            var parent = Scope(backend);
            var child = parent.CreateScope("scene-child");
            var scene = await child.LoadSceneAsync("scene");
            var firstClose = parent.CloseAsync();
            Exception failure = null;
            try { await firstClose; } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<AggregateException>(failure);
            Assert.True(scene.IsLoaded);
            Assert.AreEqual(2, parent.GetDiagnostics().Scopes.Count);
            Assert.AreEqual(1, parent.GetDiagnostics().Bundles.Count);
            Assert.Zero(backend.Unloads);
            backend.RejectSceneUnload = false;
            await parent.CloseAsync();
            Assert.AreEqual(UniTaskStatus.Faulted, firstClose.Status, "A retry must not rewrite previous waiters' result.");
            Assert.False(scene.IsLoaded);
            Assert.Zero(parent.GetDiagnostics().Scopes.Count);
            Assert.Zero(parent.GetDiagnostics().Bundles.Count);
            Assert.AreEqual(1, backend.Unloads);
        });
        [UnityTest]
        public IEnumerator PoolCallbacksCannotPublishAfterCancellingRequest() => UniTask.ToCoroutine(async () =>
        {
            foreach (bool duringActivation in new[] { false, true })
            {
                var backend = new ControlledBackend();
                var scope = Scope(backend);
                var source = new GameObject("cancel-rental-source");
                source.AddComponent<AssetReentryProbe>();
                backend.Asset.TrySetResult(source);
                var pool = scope.CreatePool("asset", new AssetPoolOptions(1));
                using var cancellation = new CancellationTokenSource();
                if (duringActivation) { AssetReentryProbe.Activated = _ => cancellation.Cancel(); }
                else { AssetReentryProbe.Rented = cancellation.Cancel; }
                try
                {
                    Assert.True((await pool.RentAsync(cancellationToken: cancellation.Token).SuppressCancellationThrow()).IsCanceled);
                    Assert.Zero(pool.ActiveCount);
                    Assert.Zero(pool.InactiveCount);
                    await scope.CloseAsync();
                    Assert.Zero(scope.GetDiagnostics().Bundles.Count);
                    Assert.Zero(scope.GetDiagnostics().PendingOperations);
                }
                finally
                {
                    AssetReentryProbe.Rented = null;
                    AssetReentryProbe.Activated = null;
                    UnityEngine.Object.Destroy(source);
                }
            }
        });
        [UnityTest]
        public IEnumerator OnEnableSelfDestructionCannotPublishDeadRental() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var source = new GameObject("self-destroy-source");
            source.AddComponent<AssetReentryProbe>();
            backend.Asset.TrySetResult(source);
            var pool = scope.CreatePool("asset", new AssetPoolOptions(1));
            AssetReentryProbe.Activated = value => UnityEngine.Object.DestroyImmediate(value);
            try
            {
                Exception failure = null;
                try { await pool.RentAsync(); } catch (Exception error) { failure = error; }
                Assert.IsInstanceOf<AssetException>(failure);
                Assert.Zero(pool.ActiveCount);
                await scope.CloseAsync();
                Assert.Zero(scope.GetDiagnostics().Bundles.Count);
            }
            finally { AssetReentryProbe.Activated = null; UnityEngine.Object.Destroy(source); }
        });
        [UnityTest]
        public IEnumerator PrewarmCancellationDrainsBeforePoolCloses() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var pool = scope.CreatePool("asset", new AssetPoolOptions(2));
            using var token = new CancellationTokenSource();
            var prewarm = pool.PrewarmAsync(2, token.Token);
            token.Cancel();
            Assert.True(await prewarm.SuppressCancellationThrow());
            var close = pool.CloseAsync();
            Assert.AreEqual(UniTaskStatus.Pending, close.Status);
            var source = new GameObject("late-prewarm");
            backend.Asset.TrySetResult(source);
            await close;
            await scope.CloseAsync();
            Assert.Zero(pool.ActiveCount);
            Assert.Zero(pool.InactiveCount);
            Assert.Zero(scope.GetDiagnostics().Bundles.Count);
            UnityEngine.Object.Destroy(source);
        });
        [UnityTest]
        public IEnumerator LateForcedResultCannotModifyNewGeneration() => UniTask.ToCoroutine(async () =>
        {
            var oldBackend = new ControlledBackend();
            var old = Scope(oldBackend);
            var request = old.LoadAsync<Texture2D>("asset");
            old.Runtime.Closed = true;
            old.Runtime.Lifetime.Cancel();
            old.ForceRelease();
            Assert.True((await request.SuppressCancellationThrow()).IsCanceled);
            var currentBackend = new ControlledBackend();
            var current = Scope(currentBackend);
            var texture = new Texture2D(1, 1);
            currentBackend.Asset.TrySetResult(texture);
            using var lease = await current.LoadAsync<Texture2D>("asset");
            oldBackend.Asset.TrySetResult(texture);
            Assert.AreSame(texture, lease.Value);
            Assert.AreEqual(1, current.GetDiagnostics().Bundles.Count);
            Assert.Zero(old.GetDiagnostics().Bundles.Count);
            await current.CloseAsync();
            UnityEngine.Object.Destroy(texture);
        });
        [UnityTest]
        public IEnumerator ForcedExitDuringInstantiationCollectsWithoutAnotherFrame() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var source = new GameObject("reentry-source");
            source.AddComponent<AssetReentryProbe>();
            backend.Asset.TrySetResult(source);
            AssetReentryProbe.Enabled = () => { scope.Runtime.Closed = true; scope.ForceRelease(); };
            try
            {
                Exception failure = null;
                try { await scope.InstantiateAsync("asset"); } catch (Exception error) { failure = error; }
                Assert.IsInstanceOf<ObjectDisposedException>(failure);
                Assert.Zero(scope.GetDiagnostics().Bundles.Count);
                Assert.Zero(scope.GetDiagnostics().Resources.Count);
                Assert.Zero(scope.GetDiagnostics().Scopes.Count);
                Assert.IsNull(GameObject.Find("reentry-source(Clone)"));
            }
            finally { AssetReentryProbe.Enabled = null; UnityEngine.Object.Destroy(source); }
        });
        [UnityTest]
        public IEnumerator ReturningCanClosePoolFromOnDisable() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var source = new GameObject("disable-source");
            source.AddComponent<AssetReentryProbe>();
            backend.Asset.TrySetResult(source);
            var pool = scope.CreatePool("asset", new AssetPoolOptions(1));
            var rental = await pool.RentAsync();
            AssetReentryProbe.Disabled = () => pool.CloseAsync().Forget();
            try
            {
                Assert.DoesNotThrow(rental.Dispose);
                await scope.CloseAsync();
                Assert.Zero(pool.ActiveCount);
                Assert.Zero(scope.GetDiagnostics().Bundles.Count);
            }
            finally { AssetReentryProbe.Disabled = null; UnityEngine.Object.Destroy(source); }
        });
        [UnityTest]
        public IEnumerator FailedPoolCallbackDestroysCandidate() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var source = new GameObject("callback-source");
            source.AddComponent<AssetReentryProbe>();
            backend.Asset.TrySetResult(source);
            var pool = scope.CreatePool("asset", new AssetPoolOptions(1));
            AssetReentryProbe.Rented = () => throw new InvalidOperationException("callback failure");
            try
            {
                Exception failure = null;
                try { await pool.RentAsync(); } catch (Exception error) { failure = error; }
                Assert.IsInstanceOf<InvalidOperationException>(failure);
                await scope.CloseAsync();
                Assert.Zero(pool.ActiveCount);
                Assert.Zero(pool.InactiveCount);
                Assert.Zero(scope.GetDiagnostics().Bundles.Count);
            }
            finally { AssetReentryProbe.Rented = null; UnityEngine.Object.Destroy(source); }
        });
        [UnityTest]
        public IEnumerator CloseWaitsForAlreadyStartedSceneAfterCallerCancels() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            using var cancellation = new CancellationTokenSource();
            var request = scope.LoadSceneAsync("scene", cancellation.Token);
            Assert.True(backend.SceneStarted);
            cancellation.Cancel();
            Assert.True((await request.SuppressCancellationThrow()).IsCanceled);
            var closing = scope.CloseAsync();
            Assert.AreEqual(UniTaskStatus.Pending, closing.Status);
            backend.SceneGate.TrySetResult();
            await closing;
            Assert.False(backend.Scene.isLoaded);
            Assert.Zero(scope.GetDiagnostics().Bundles.Count);
            Assert.AreEqual(1, backend.Unloads);
        });
        [UnityTest]
        public IEnumerator AllCancelledAssetWaitersDrainAndCollectLateResult() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var pending = scope.LoadAsync<Texture2D>("asset");
            var closing = scope.CloseAsync();
            Assert.True((await pending.SuppressCancellationThrow()).IsCanceled);
            Assert.AreEqual(UniTaskStatus.Pending, closing.Status);
            var texture = new Texture2D(1, 1);
            backend.Asset.TrySetResult(texture);
            await closing;
            Assert.AreEqual(1, backend.Loads);
            Assert.AreEqual(1, backend.Unloads);
            Assert.Zero(scope.GetDiagnostics().Resources.Count);
            UnityEngine.Object.Destroy(texture);
        });
        [UnityTest]
        public IEnumerator CompletionCanReleaseAndReloadWithoutOldFinallyRemovingSuccessor() => UniTask.ToCoroutine(async () =>
        {
            var backend = new ControlledBackend();
            var scope = Scope(backend);
            var texture = new Texture2D(1, 1);
            var first = scope.LoadAsync<Texture2D>("asset");
            AssetLease<Texture2D> successor = null;
            async UniTask Reenter()
            {
                var lease = await first;
                lease.Dispose();
                successor = await scope.LoadAsync<Texture2D>("asset");
            }
            var consumer = Reenter();
            backend.Asset.TrySetResult(texture);
            await consumer;
            Assert.AreSame(texture, successor.Value);
            Assert.AreEqual(1, scope.GetDiagnostics().Resources.Count);
            successor.Dispose();
            await scope.CloseAsync();
            Assert.AreEqual(backend.Loads, backend.Unloads);
            UnityEngine.Object.Destroy(texture);
        });
    }
}
