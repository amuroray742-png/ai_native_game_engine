using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UnityCore.Assets.Editor
{
    public enum EditorAssetLoadMode { AssetBundle = 0, AssetDatabase = 1 }

    public static class EditorAssets
    {
        /// <summary>Captures the configuration's Editor mode when creating a module; changing it does not switch a running module.</summary>
        public static AssetsModule CreateConfigured(AssetGroups groups, string contentRoot = null, AssetRuntimeOptions options = null)
        {
            if (groups == null) { throw new ArgumentNullException(nameof(groups)); }
            return Create(groups, groups.EditorLoadMode, contentRoot, options);
        }

        /// <summary>Explicit Editor mode selection. Bundle mode does not need groups; direct mode requires them and ignores contentRoot.</summary>
        public static AssetsModule Create(AssetGroups groups, EditorAssetLoadMode mode, string contentRoot = null, AssetRuntimeOptions options = null)
        {
            switch (mode)
            {
                case EditorAssetLoadMode.AssetBundle: return new AssetsModule(contentRoot, options ?? new AssetRuntimeOptions());
                case EditorAssetLoadMode.AssetDatabase:
                    if (groups == null) { throw new ArgumentNullException(nameof(groups)); }
                    return CreateWithOptions(groups, options);
                default: throw new AssetException("InvalidEditorLoadMode", "Unknown Editor resource load mode: " + mode);
            }
        }

        /// <summary>Legacy direct-loading entry point; always uses AssetDatabase regardless of the configuration mode.</summary>
        public static AssetsModule Create(AssetGroups groups) => CreateWithOptions(groups, null);
        public static AssetsModule CreateWithOptions(AssetGroups groups, AssetRuntimeOptions options)
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
            }, options);
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
