using System.Collections;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class AssetPlayCycleTests
    {
        private static bool ReloadEnabled() => !EditorSettings.enterPlayModeOptionsEnabled ||
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;

        [Test]
        public void EditorModeDefaultsAndInvalidInputs()
        {
            var groups = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                Assert.AreEqual(EditorAssetLoadMode.AssetBundle, groups.EditorLoadMode);
                EditorJsonUtility.FromJsonOverwrite("{}", groups);
                Assert.AreEqual(EditorAssetLoadMode.AssetBundle, groups.EditorLoadMode);
                Assert.Throws<System.ArgumentNullException>(() => EditorAssets.CreateConfigured(null));
                Assert.Throws<System.ArgumentNullException>(() => EditorAssets.Create(null, EditorAssetLoadMode.AssetDatabase));
                Assert.Throws<AssetException>(() => EditorAssets.Create(groups, (EditorAssetLoadMode)99));
                Assert.IsNotNull(EditorAssets.Create(null, EditorAssetLoadMode.AssetBundle));
            }
            finally { Object.DestroyImmediate(groups); }
        }

        [Test]
        public void ConfiguredBundleModeHonorsExplicitDirectoryWithoutFallback()
        {
            var groups = Object.Instantiate(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"));
            try
            {
                groups.EditorLoadMode = EditorAssetLoadMode.AssetBundle;
                var module = EditorAssets.CreateConfigured(groups, System.IO.Path.Combine(Application.temporaryCachePath, System.Guid.NewGuid().ToString("N")));
                var error = Assert.Throws<AssetException>(() => module.Initialize(null, System.Threading.CancellationToken.None));
                Assert.AreEqual("CatalogReadFailed", error.Code);
            }
            finally { Object.DestroyImmediate(groups); }
        }

        [UnityTest]
        public IEnumerator ConfiguredDirectModeCapturesSelectionAndIgnoresBundleDirectory()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return UniTask.ToCoroutine(async () =>
            {
                var groups = Object.Instantiate(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"));
                try
                {
                    groups.EditorLoadMode = EditorAssetLoadMode.AssetDatabase;
                    var module = EditorAssets.CreateConfigured(groups, "MissingBundles-" + System.Guid.NewGuid().ToString("N"));
                    groups.EditorLoadMode = EditorAssetLoadMode.AssetBundle;
                    Core.Inst.RegisterModule(module);
                    await Core.Inst.RunAsync();
                    var lease = await module.LoadAsync<GameObject>("Assets/Probe/A");
                    var source = lease.Value;
                    await module.InstantiateAsync("Assets/Probe/A");
                    var pool = module.CreatePool("Assets/Probe/B", new AssetPoolOptions(1));
                    await pool.PrewarmAsync(1);
                    await pool.RentAsync();
                    await module.LoadSceneAsync("Assets/Probe/Content");
                    await Core.Inst.CloseAsync();
                    Assert.IsTrue(source != null);
                    Assert.AreSame(source, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Probe/A.prefab"));
                    Assert.Zero(module.GetDiagnostics().Bundles.Count);
                    Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles());
                }
                finally
                {
                    await Core.Inst.CloseAsync();
                    Object.Destroy(groups);
                }
            });
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator DirectModeReleasesOwnershipWithoutDestroyingSource()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return UniTask.ToCoroutine(async () =>
            {
                var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
                var module = EditorAssets.Create(groups);
                Core.Inst.RegisterModule(module);
                await Core.Inst.RunAsync();
                var lease = await module.LoadAsync<GameObject>("Assets/Probe/A");
                var source = lease.Value;
                await Core.Inst.CloseAsync();
                Assert.IsTrue(source != null);
                Assert.AreSame(source, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Probe/A.prefab"));
                Assert.Zero(module.GetDiagnostics().Bundles.Count);
            });
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator PlayExitWithBorrowedObjectsReleasesRealBundlesAcrossSessions()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return LeaveOwnedObjects().ToCoroutine();
            yield return new ExitPlayMode();
            Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles());
            Assert.IsFalse(Resources.FindObjectsOfTypeAll<GameObject>().Any(x => x.name.StartsWith("AssetPool:")));
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return LeaveOwnedObjects().ToCoroutine();
            yield return new ExitPlayMode();
            Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles());
        }

        private static async UniTask LeaveOwnedObjects()
        {
            var module = new AssetsModule();
            Core.Inst.RegisterModule(module);
            await Core.Inst.RunAsync();
            var pool = module.CreatePool("Assets/Probe/A", new AssetPoolOptions(1));
            await pool.PrewarmAsync(1);
            await pool.RentAsync();
            await module.InstantiateAsync("Assets/Probe/B");
            await module.LoadSceneAsync("Assets/Probe/Content");
        }
    }
}
