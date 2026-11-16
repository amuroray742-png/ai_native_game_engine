using System;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UIDemo.Acceptance
{
    // Real native loading, followed by controlled delivery latency for one resource.
    // The bundle remains owned by AssetRuntime while the result is held here.
    internal sealed class DelayedGalleryBackend : IAssetBackend
    {
        private readonly BundleBackend _inner;
        private readonly UniTaskCompletionSource _delivery = new UniTaskCompletionSource();
        internal bool Held { get; private set; }
        internal bool Released { get; private set; }
        internal DelayedGalleryBackend(string root) { _inner = new BundleBackend(root); }
        public UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry) => _inner.LoadBundleAsync(entry);
        public void Unload(AssetBundle bundle) => _inner.Unload(bundle);
        public async UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset)
        {
            var value = await _inner.LoadAsync(bundle, entry, type, subAsset);
            if (entry.Address == UIDemoBootstrap.Content + nameof(GalleryView))
            {
                Held = true;
                await _delivery.Task;
                Held = false;
            }
            return value;
        }
        // Called after Core retirement, to exercise disposal of a late successful result.
        internal void ReleaseDelivery() { Released = true; _delivery.TrySetResult(); }
        public UniTask<Scene> LoadSceneAsync(AssetEntry entry) => _inner.LoadSceneAsync(entry);
        public UniTask UnloadSceneAsync(Scene scene) => _inner.UnloadSceneAsync(scene);
    }
}
