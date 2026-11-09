using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UnityCore.Assets
{
    internal interface IAssetBackend
    {
        UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry);
        void Unload(AssetBundle bundle);
        UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset);
        UniTask<Scene> LoadSceneAsync(AssetEntry entry);
        UniTask UnloadSceneAsync(Scene scene);
    }

    internal sealed class BundleBackend : IAssetBackend
    {
        private readonly string _root;
        internal BundleBackend(string root) { _root = Path.GetFullPath(root); }
        public async UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry)
        {
            string path = Path.Combine(_root, entry.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Size)
            {
                throw new AssetException("BundleFileInvalid", "Bundle missing or size differs.", bundle: entry.Name, stage: "bundle");
            }
            // Unity interprets zero CRC as "do not verify", including a missing JSON field.
            // Keep integrity verification for that rare case without rejecting legitimate zero CRCs.
            if (entry.Crc == 0)
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(digest, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                { throw new AssetException("BundleHashMismatch", "Zero-CRC bundle digest differs.", bundle: entry.Name, stage: "bundle"); }
            }
            var bundle = await AssetBundle.LoadFromFileAsync(path, entry.Crc);
            if (bundle == null) { throw new AssetException("BundleLoadFailed", "Unity rejected bundle.", bundle: entry.Name, stage: "bundle"); }
            return bundle;
        }
        public void Unload(AssetBundle bundle) { if (bundle != null) { bundle.Unload(true); } }
        public async UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset)
        {
            if (subAsset == null) { return await bundle.LoadAssetAsync(entry.Path, type); }
            var request = bundle.LoadAssetWithSubAssetsAsync(entry.Path, type);
            await request;
            var matches = request.allAssets.Where(item => type.IsInstanceOfType(item) && item.name == subAsset).ToArray();
            if (matches.Length != 1) { throw new AssetException("SubAssetAmbiguous", "Expected one matching sub asset.", entry.Address, entry.Bundle, "asset"); }
            return matches[0];
        }
        public async UniTask<Scene> LoadSceneAsync(AssetEntry entry)
        {
            return await SceneLoadOperation.LoadAsync(entry.Path, () => SceneManager.LoadSceneAsync(entry.Path, LoadSceneMode.Additive));
        }
        public async UniTask UnloadSceneAsync(Scene scene) { await SceneManager.UnloadSceneAsync(scene); }
    }
}
