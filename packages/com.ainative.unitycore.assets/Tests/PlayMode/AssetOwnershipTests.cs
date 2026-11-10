using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using AiNative.UnityCore.Interfaces;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class AssetOwnershipTests
    {
        private Core _core;
        private AssetsModule _assets;
        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            _core = Core.Inst;
            await _core.CloseAsync();
            // Each test session registers a fresh service only once; module instances survive Restart.
            try { _assets = (AssetsModule)_core.GetModule<IAssets>(); }
            catch (InvalidOperationException)
            {
                _assets = new AssetsModule(Path.Combine(Application.streamingAssetsPath, "AiNativeAssets"));
                _core.RegisterModule(_assets);
            }
            await _core.RunAsync();
        });
        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            await _core.CloseAsync();
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
            Assert.Zero(_assets.GetDiagnostics().PendingOperations);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });
        [UnityTest]
        public IEnumerator ZeroCrcCannotDisableBundleIntegrityChecking() => UniTask.ToCoroutine(async () =>
        {
            string source = Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
            var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(source, "catalog.json")));
            var entry = catalog.Bundles.Single(x => x.Name == catalog.Assets.Single(a => a.Address == "Assets/Probe/A").Bundle);
            string target = Path.Combine(Application.temporaryCachePath, "ZeroCrc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            string path = Path.Combine(target, entry.Name);
            File.Copy(Path.Combine(source, entry.Name), path);
            entry.Crc = 0;
            var backend = new BundleBackend(target);
            var bundle = await backend.LoadBundleAsync(entry);
            Assert.NotNull(bundle);
            backend.Unload(bundle);
            using (var stream = File.OpenWrite(path)) { stream.Write(new byte[32], 0, 32); }
            Exception failure = null;
            try { await backend.LoadBundleAsync(entry); } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<AssetException>(failure);
            Assert.AreEqual("BundleHashMismatch", ((AssetException)failure).Code);
        });
        [UnityTest]
        public IEnumerator DependenciesRemainOwnedThroughoutBusinessOnDestroy() => UniTask.ToCoroutine(async () =>
        {
            var instance = await _assets.InstantiateAsync("Assets/Probe/A");
            instance.Value.AddComponent<AssetReentryProbe>();
            var material = instance.Value.GetComponent<Renderer>().sharedMaterial;
            bool called = false;
            bool owned = false;
            AssetReentryProbe.Destroyed = () =>
            {
                called = true;
                owned = material != null && material.mainTexture != null && _assets.GetDiagnostics().Bundles.Count > 0;
            };
            try
            {
                instance.Dispose();
                await instance.Destroyed;
                Assert.True(called);
                Assert.True(owned, "Business OnDestroy must finish before dependencies are released.");
                Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
            }
            finally { AssetReentryProbe.Destroyed = null; }
        });
        [UnityTest]
        public IEnumerator WrongTypeRollsBackAndCanRetryCorrectType() => UniTask.ToCoroutine(async () =>
        {
            Exception failure = null;
            try { await _assets.LoadAsync<AudioClip>("Assets/Probe/A"); } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<AssetException>(failure);
            Assert.AreEqual("AssetTypeMismatch", ((AssetException)failure).Code);
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
            using var valid = await _assets.LoadAsync<GameObject>("Assets/Probe/A");
            Assert.NotNull(valid.Value);
        });
        [UnityTest]
        public IEnumerator CorruptRealBundleRollsBackDependencyPins() => UniTask.ToCoroutine(async () =>
        {
            string source = Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
            var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(source, "catalog.json")));
            string target = Path.Combine(Application.temporaryCachePath, "CorruptAssets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            foreach (var bundle in catalog.Bundles) { File.Copy(Path.Combine(source, bundle.Name), Path.Combine(target, bundle.Name)); }
            string name = catalog.Assets.Single(x => x.Address == "Assets/Probe/A").Bundle;
            using (var stream = File.OpenWrite(Path.Combine(target, name))) { stream.Write(new byte[32], 0, 32); }
            var scope = new AssetScope(new AssetRuntime(catalog, new BundleBackend(target)), "corrupt");
            bool previous = LogAssert.ignoreFailingMessages;
            Exception failure = null;
            try
            {
                // Unity emits platform-dependent native corruption messages; the stable managed result is asserted below.
                LogAssert.ignoreFailingMessages = true;
                try { await scope.LoadAsync<GameObject>("Assets/Probe/A"); } catch (Exception error) { failure = error; }
                await scope.CloseAsync();
            }
            finally { LogAssert.ignoreFailingMessages = previous; }
            Assert.IsInstanceOf<AssetException>(failure);
            Assert.AreEqual("BundleLoadFailed", ((AssetException)failure).Code);
            Assert.AreEqual("Assets/Probe/A", ((AssetException)failure).Address);
            Assert.AreEqual(name, ((AssetException)failure).Bundle);
            Assert.AreEqual("bundle", ((AssetException)failure).Stage);
            Assert.NotNull(failure.InnerException);
            Assert.Zero(scope.GetDiagnostics().Bundles.Count);
            Assert.Zero(scope.GetDiagnostics().PendingOperations);
        });
        [UnityTest]
        public IEnumerator ExternalSceneUnloadReleasesSharedDependencies() => UniTask.ToCoroutine(async () =>
        {
            var instance = await _assets.InstantiateAsync("Assets/Probe/B");
            var scene = await _assets.LoadSceneAsync("Assets/Probe/Content");
            var renderer = scene.Value.GetRootGameObjects()[0].GetComponentInChildren<Renderer>();
            Assert.AreSame(instance.Value.GetComponent<Renderer>().sharedMaterial, renderer.sharedMaterial);
            await UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene.Value);
            Assert.False(scene.IsLoaded);
            Assert.IsTrue(instance.Value.GetComponent<Renderer>().sharedMaterial != null);
            instance.Dispose();
            await instance.Destroyed;
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
        });
        [UnityTest]
        public IEnumerator AudioAndSpriteSubAssetsLoadFromRealBundles() => UniTask.ToCoroutine(async () =>
        {
            using var audio = await _assets.LoadAsync<AudioClip>("Assets/Probe/Tone");
            using var sprite = await _assets.LoadSubAssetAsync<Sprite>("Assets/Probe/SpriteSet", "icon");
            Assert.Greater(audio.Value.samples, 0);
            Assert.AreEqual("icon", sprite.Value.name);
            Assert.NotNull(sprite.Value.texture);
        });
        [UnityTest]
        public IEnumerator SharedMaterialLivesUntilLastOwner() => UniTask.ToCoroutine(async () =>
        {
            var a = await _assets.InstantiateAsync("Assets/Probe/A");
            var b = await _assets.InstantiateAsync("Assets/Probe/B");
            var material = a.Value.GetComponent<Renderer>().sharedMaterial;
            Assert.AreSame(material, b.Value.GetComponent<Renderer>().sharedMaterial);
            Assert.AreSame(material.mainTexture, b.Value.GetComponent<Renderer>().sharedMaterial.mainTexture);
            a.Dispose();
            await a.Destroyed;
            Assert.IsTrue(material != null);
            b.Dispose();
            await b.Destroyed;
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
        });
        [UnityTest]
        public IEnumerator ConcurrentRequestsOwnIndependentLeases() => UniTask.ToCoroutine(async () =>
        {
            var one = _assets.LoadAsync<GameObject>("Assets/Probe/A");
            var two = _assets.LoadAsync<GameObject>("Assets/Probe/A");
            var a = await one;
            var b = await two;
            Assert.AreSame(a.Value, b.Value);
            a.Dispose(); a.Dispose();
            Assert.IsTrue(b.Value != null);
            Assert.Throws<ObjectDisposedException>(() => { _ = a.Value; });
            b.Dispose();
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
        });
        [UnityTest]
        public IEnumerator OneCancelledWaiterDoesNotCancelAnother() => UniTask.ToCoroutine(async () =>
        {
            using var cancel = new CancellationTokenSource();
            var one = _assets.LoadAsync<GameObject>("Assets/Probe/A", cancel.Token);
            var two = _assets.LoadAsync<GameObject>("Assets/Probe/A");
            cancel.Cancel();
            Assert.IsTrue(await one.SuppressCancellationThrow() is var result && result.IsCanceled);
            using var survivor = await two;
            Assert.IsNotNull(survivor.Value);
        });
        [UnityTest]
        public IEnumerator ScopeCancelsAndDrainsSceneRequest() => UniTask.ToCoroutine(async () =>
        {
            var scope = _assets.CreateScope("scene-test");
            using var cancel = new CancellationTokenSource();
            var request = scope.LoadSceneAsync("Assets/Probe/Content", cancel.Token);
            cancel.Cancel();
            await request.SuppressCancellationThrow();
            await scope.CloseAsync();
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
            Assert.Zero(_assets.GetDiagnostics().PendingOperations);
        });
        [UnityTest]
        public IEnumerator NeverActivatedPrewarmObjectsAreReleased() => UniTask.ToCoroutine(async () =>
        {
            var pool = _assets.CreatePool("Assets/Probe/A", new AssetPoolOptions(2));
            await pool.PrewarmAsync(2);
            Assert.AreEqual(2, pool.InactiveCount);
            await pool.CloseAsync();
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
        });
        [UnityTest]
        public IEnumerator RentalIsIdempotentAndCannotReturnSuccessor() => UniTask.ToCoroutine(async () =>
        {
            var pool = _assets.CreatePool("Assets/Probe/A", new AssetPoolOptions(1));
            var first = await pool.RentAsync();
            var value = first.Value;
            first.Dispose();
            var next = await pool.RentAsync();
            Assert.AreSame(value, next.Value);
            first.Dispose();
            Assert.AreEqual(1, pool.ActiveCount);
            Assert.IsTrue(next.Value.activeSelf);
            await pool.CloseAsync();
            Assert.Throws<ObjectDisposedException>(() => { _ = next.Value; });
        });
        [UnityTest]
        public IEnumerator RepeatedLeasesAndScopesLeaveNoHistoricalRecords() => UniTask.ToCoroutine(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                var scope = _assets.CreateScope("transient");
                using (var lease = await scope.LoadAsync<GameObject>("Assets/Probe/A")) { Assert.IsNotNull(lease.Value); }
                await scope.CloseAsync();
            }
            Assert.AreEqual(1, _assets.GetDiagnostics().Scopes.Count);
            StringAssert.Contains("leases=0", _assets.GetDiagnostics().Scopes[0]);
        });
        [UnityTest]
        public IEnumerator PoolCapacityDestroysOverflow() => UniTask.ToCoroutine(async () =>
        {
            var pool = _assets.CreatePool("Assets/Probe/A", new AssetPoolOptions(1));
            var one = await pool.RentAsync();
            var two = await pool.RentAsync();
            var overflow = two.Value;
            one.Dispose(); two.Dispose();
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield();
            Assert.AreEqual(1, pool.InactiveCount);
            Assert.True(overflow == null);
            await pool.CloseAsync();
        });
        [UnityTest]
        public IEnumerator ExternalDestroyInvalidatesPoolRentalAndIdleCapacity() => UniTask.ToCoroutine(async () =>
        {
            var pool = _assets.CreatePool("Assets/Probe/A", new AssetPoolOptions(1));
            var rental = await pool.RentAsync();
            UnityEngine.Object.Destroy(rental.Value);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield();
            Assert.Zero(pool.ActiveCount);
            Assert.Throws<ObjectDisposedException>(() => { _ = rental.Value; });
            rental.Dispose();
            var second = await pool.RentAsync();
            var value = second.Value;
            second.Dispose();
            UnityEngine.Object.Destroy(value);
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            await UniTask.Yield();
            Assert.Zero(pool.InactiveCount);
            await pool.PrewarmAsync(1);
            Assert.AreEqual(1, pool.InactiveCount);
            await pool.CloseAsync();
        });
        [UnityTest]
        public IEnumerator RestartInvalidatesOldLeases() => UniTask.ToCoroutine(async () =>
        {
            var old = await _assets.LoadAsync<GameObject>("Assets/Probe/A");
            await _core.RestartAsync();
            Assert.Throws<ObjectDisposedException>(() => { _ = old.Value; });
            using var current = await _assets.LoadAsync<GameObject>("Assets/Probe/A");
            old.Dispose();
            Assert.IsNotNull(current.Value);
        });
        [UnityTest]
        public IEnumerator SceneRejectsDuplicateAndReleasesOnUnload() => UniTask.ToCoroutine(async () =>
        {
            var scene = await _assets.LoadSceneAsync("Assets/Probe/Content");
            Exception failure = null;
            try { await _assets.LoadSceneAsync("Assets/Probe/Content"); } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<AssetException>(failure);
            scene.SetActive();
            await scene.UnloadAsync();
            await scene.UnloadAsync();
            Assert.Zero(_assets.GetDiagnostics().Bundles.Count);
        });
    }
}
