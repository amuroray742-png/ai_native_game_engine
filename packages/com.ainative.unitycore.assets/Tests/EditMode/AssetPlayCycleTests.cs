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
                var lease = await module.LoadAsync<GameObject>("a");
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
            var pool = module.CreatePool("a", new AssetPoolOptions(1));
            await pool.PrewarmAsync(1);
            await pool.RentAsync();
            await module.InstantiateAsync("b");
            await module.LoadSceneAsync("scene");
        }
    }
}
