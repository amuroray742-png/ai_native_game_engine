using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UnityCore.Assets.Editor
{
    public static class EditorAssets
    {
        public static AssetsModule Create(AssetGroups groups)
        {
            return new AssetsModule(() =>
            {
                var plan = AssetBuildPlan.Create(groups);
                var catalog = new AssetCatalog
                {
                    UnityVersion = Application.unityVersion,
                    Assets = plan.Addresses,
                    Bundles = plan.Definitions.Select(x => new BundleEntry { Name = x.assetBundleName, Dependencies = plan.Dependencies[x.assetBundleName].OrderBy(y => y, StringComparer.Ordinal).ToArray() }).ToArray()
                };
                return (catalog, new EditorBackend());
            });
        }
        private sealed class EditorBackend : IAssetBackend
        {
            public UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry) => UniTask.FromResult<AssetBundle>(null);
            public void Unload(AssetBundle bundle) { }
            public UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset)
            {
                if (subAsset == null) { return UniTask.FromResult(AssetDatabase.LoadAssetAtPath(entry.Path, type)); }
                var matches = AssetDatabase.LoadAllAssetsAtPath(entry.Path).Where(x => type.IsInstanceOfType(x) && x.name == subAsset).ToArray();
                if (matches.Length != 1) { throw new AssetException("SubAssetAmbiguous", "Expected one matching sub asset.", entry.Address); }
                return UniTask.FromResult(matches[0]);
            }
            public async UniTask<Scene> LoadSceneAsync(AssetEntry entry)
            {
                return await SceneLoadOperation.LoadAsync(entry.Path, () => EditorSceneManager.LoadSceneAsyncInPlayMode(entry.Path, new LoadSceneParameters(LoadSceneMode.Additive)));
            }
            public async UniTask UnloadSceneAsync(Scene scene) { await SceneManager.UnloadSceneAsync(scene); }
        }
    }
}
