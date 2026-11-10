using System;
using System.Collections;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using AiNative.UnityCore.Assets.Editor;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class EditorModeOwnershipTests
    {
        private static bool ReloadEnabled() => !EditorSettings.enterPlayModeOptionsEnabled ||
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;

        private static IEnumerator InBothModes(Func<AssetsModule, EditorAssetLoadMode, UniTask> check)
        {
            return UniTask.ToCoroutine(async () =>
            {
                var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
                foreach (var mode in new[] { EditorAssetLoadMode.AssetDatabase, EditorAssetLoadMode.AssetBundle })
                {
                    var module = EditorAssets.Create(groups, mode);
                    await module.Initialize(null, CancellationToken.None);
                    try { await check(module, mode); }
                    finally
                    {
                        await module.ShutdownAsync(null, CancellationToken.None);
                        module.Shut(null);
                    }
                    AssertEmpty(module);
                    Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles());
                }
            });
        }

        private static void AssertEmpty(AssetsModule module)
        {
            var state = module.GetDiagnostics();
            Assert.IsEmpty(state.Resources);
            Assert.IsEmpty(state.Bundles);
            Assert.IsEmpty(state.Scopes);
            Assert.IsEmpty(state.Pools);
            Assert.Zero(state.PendingOperations);
        }

        [UnityTest]
        public IEnumerator IndependentLeasesCancellationAndFailureRollback()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return InBothModes(CheckIndependentLeasesCancellationAndFailureRollback);
            yield return new ExitPlayMode();
        }

        private static async UniTask CheckIndependentLeasesCancellationAndFailureRollback(AssetsModule module, EditorAssetLoadMode mode)
        {
            var scope = module.CreateScope("leases");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.IsTrue((await scope.LoadAsync<GameObject>("Assets/Probe/A", cancelled.Token).SuppressCancellationThrow()).IsCanceled);
            Assert.IsEmpty(module.GetDiagnostics().Resources);
            using var late = new CancellationTokenSource();
            var first = scope.LoadAsync<GameObject>("Assets/Probe/A", late.Token);
            var second = scope.LoadAsync<GameObject>("Assets/Probe/A");
            bool completed = first.Status == UniTaskStatus.Succeeded;
            late.Cancel();
            var result = await first.SuppressCancellationThrow();
            var survivor = await second;
            Assert.AreEqual(!completed, result.IsCanceled, mode.ToString());
            if (!result.IsCanceled)
            {
                Assert.AreSame(result.Result.Value, survivor.Value);
                result.Result.Dispose(); result.Result.Dispose();
            }
            Assert.IsTrue(survivor.Value != null);
            var duplicate = await scope.LoadAsync<GameObject>("Assets/Probe/A");
            Assert.IsTrue(module.GetDiagnostics().Resources.Single().Contains("refs=2,"));
            survivor.Dispose(); survivor.Dispose();
            Assert.IsTrue(duplicate.Value != null);
            duplicate.Dispose();
            Assert.IsEmpty(module.GetDiagnostics().Resources);
            Assert.IsEmpty(module.GetDiagnostics().Bundles);
            Exception failure = null;
            try { await scope.LoadAsync<AudioClip>("Assets/Probe/A"); } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<AssetException>(failure);
            Assert.IsEmpty(module.GetDiagnostics().Bundles);
            var sprite = await scope.LoadSubAssetAsync<Sprite>("Assets/Probe/SpriteSet", "icon");
            Assert.NotNull(sprite.Value.texture);
            await scope.CloseAsync();
            Assert.Throws<ObjectDisposedException>(() => { _ = sprite.Value; });
            Assert.IsEmpty(module.GetDiagnostics().Resources);
        }

        [UnityTest]
        public IEnumerator SceneCancellationAndExternalUnloadDrainOwnership()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return InBothModes(CheckSceneCancellationAndExternalUnloadDrainOwnership);
            yield return new ExitPlayMode();
        }

        private static async UniTask CheckSceneCancellationAndExternalUnloadDrainOwnership(AssetsModule module, EditorAssetLoadMode mode)
        {
            var scope = module.CreateScope("cancel-scene");
            var pending = scope.LoadSceneAsync("Assets/Probe/Content");
            var closing = scope.CloseAsync();
            Assert.IsTrue((await pending.SuppressCancellationThrow()).IsCanceled);
            await closing;
            Assert.IsFalse(SceneManager.GetSceneByPath("Assets/Probe/Content.unity").isLoaded);
            Assert.IsEmpty(module.GetDiagnostics().Bundles);
            Assert.Zero(module.GetDiagnostics().PendingOperations);
            var b = await module.InstantiateAsync("Assets/Probe/B");
            var material = b.Value.GetComponent<Renderer>().sharedMaterial;
            var scene = await module.LoadSceneAsync("Assets/Probe/Content");
            Assert.AreSame(material, scene.Value.GetRootGameObjects()[0].GetComponentInChildren<Renderer>().sharedMaterial);
            await SceneManager.UnloadSceneAsync(scene.Value);
            Assert.IsFalse(scene.IsLoaded);
            Assert.IsTrue(material != null);
            b.Dispose();
            await b.Destroyed;
            Assert.IsEmpty(module.GetDiagnostics().Bundles);
        }

        [UnityTest]
        public IEnumerator ScopeCloseAndNewGenerationInvalidateOldOwners()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return InBothModes(CheckScopeCloseAndNewGenerationInvalidateOldOwners);
            yield return new ExitPlayMode();
        }

        private static async UniTask CheckScopeCloseAndNewGenerationInvalidateOldOwners(AssetsModule module, EditorAssetLoadMode mode)
        {
            var scope = module.CreateScope("close-request");
            var pending = scope.LoadAsync<GameObject>("Assets/Probe/A");
            var close = scope.CloseAsync();
            var result = await pending.SuppressCancellationThrow();
            await close;
            if (!result.IsCanceled) { Assert.Throws<ObjectDisposedException>(() => { _ = result.Result.Value; }); }
            Assert.IsEmpty(module.GetDiagnostics().Resources);
            Assert.IsEmpty(module.GetDiagnostics().Bundles);
            var instance = await module.InstantiateAsync("Assets/Probe/A");
            UnityEngine.Object.Destroy(instance.Value);
            await instance.Destroyed;
            Assert.IsFalse(instance.IsAlive);
            Assert.IsEmpty(module.GetDiagnostics().Resources);
            var old = await module.LoadAsync<GameObject>("Assets/Probe/B");
            var source = old.Value;
            await module.ShutdownAsync(null, CancellationToken.None);
            module.Shut(null);
            AssertEmpty(module);
            Assert.Throws<ObjectDisposedException>(() => { _ = old.Value; });
            Assert.AreEqual(mode == EditorAssetLoadMode.AssetDatabase, source != null);
            await module.Initialize(null, CancellationToken.None);
            using var current = await module.LoadAsync<GameObject>("Assets/Probe/B");
            old.Dispose();
            Assert.IsTrue(current.Value != null);
            Assert.IsTrue(module.GetDiagnostics().Resources.Single().Contains("refs=1,"));
        }

        [UnityTest]
        public IEnumerator PoolCloseWaitsForRootAndBorrowedObjects()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return InBothModes(CheckPoolCloseWaitsForRootAndBorrowedObjects);
            yield return new ExitPlayMode();
        }

        private static async UniTask CheckPoolCloseWaitsForRootAndBorrowedObjects(AssetsModule module, EditorAssetLoadMode mode)
        {
            var empty = module.CreatePool("Assets/Probe/A", new AssetPoolOptions(1));
            var emptyRoot = Resources.FindObjectsOfTypeAll<GameObject>().Single(x => x.name == "AssetPool:Assets/Probe/A");
            await empty.CloseAsync();
            Assert.IsTrue(emptyRoot == null, mode + ": CloseAsync must wait for the pool root's actual destruction.");
            var pool = module.CreatePool("Assets/Probe/A", new AssetPoolOptions(2));
            await pool.PrewarmAsync(2);
            var rented = await pool.RentAsync();
            var instance = rented.Value;
            await module.ShutdownAsync(null, CancellationToken.None);
            Assert.IsTrue(instance == null);
            Assert.Throws<ObjectDisposedException>(() => { _ = rented.Value; });
            rented.Dispose();
            Assert.IsFalse(Resources.FindObjectsOfTypeAll<GameObject>().Any(x => x.name.StartsWith("AssetPool:")));
            AssertEmpty(module);
            if (mode == EditorAssetLoadMode.AssetDatabase)
            { Assert.IsTrue(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Probe/A.prefab") != null); }
        }
    }
}
