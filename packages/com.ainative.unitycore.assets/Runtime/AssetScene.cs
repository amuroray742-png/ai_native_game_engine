using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetScene
    {
        private readonly AssetRuntime _runtime;
        private readonly AssetEntry _entry;
        private readonly AssetRuntime.BundlePin _pin;
        private readonly Scene _scene;
        private readonly System.Func<AssetPriority> _demand;
        private bool _released;
        private UniTaskCompletionSource _unloaded;
        internal System.Action Released;
        internal AssetScene(AssetRuntime runtime, AssetEntry entry, AssetRuntime.BundlePin pin, Scene scene, System.Func<AssetPriority> demand = null)
        {
            _runtime = runtime; _entry = entry; _pin = pin; _scene = scene;
            _demand = demand;
            SceneManager.sceneUnloaded += OnUnloaded;
        }
        public bool IsLoaded => !_released && _scene.IsValid() && _scene.isLoaded;
        public Scene Value => IsLoaded ? _scene : throw new System.ObjectDisposedException(nameof(AssetScene));
        public void SetActive()
        {
            if (!IsLoaded || !SceneManager.SetActiveScene(_scene)) { throw new AssetException("SceneNotLoaded", "Cannot activate unloaded scene.", _entry.Address); }
        }
        public UniTask UnloadAsync()
        {
            if (_released) { return UniTask.CompletedTask; }
            if (_unloaded != null && _unloaded.Task.Status != UniTaskStatus.Faulted) { return _unloaded.Task; }
            var completion = new UniTaskCompletionSource();
            _unloaded = completion;
            UnloadOperationAsync(completion).Forget();
            return completion.Task;
        }
        private async UniTask UnloadOperationAsync(UniTaskCompletionSource completion)
        {
            try
            {
                if (IsLoaded) { await _runtime.Backend.UnloadSceneAsync(_scene); }
                if (IsLoaded) { throw new AssetException("SceneUnloadFailed", "Scene is still loaded after unload completed.", _entry.Address, _entry.Bundle, "unload"); }
                Release();
                completion.TrySetResult();
            }
            catch (System.Exception error)
            {
                var failure = AssetException.WithContext(error, _entry.Address, _entry.Bundle, "unload");
                _runtime.RecordError(failure);
                completion.TrySetException(failure);
            }
        }
        private void OnUnloaded(Scene scene) { if (scene.handle == _scene.handle) { Release(); } }
        private void Release()
        {
            if (_released) { return; }
            _released = true;
            SceneManager.sceneUnloaded -= OnUnloaded;
            _runtime.Scenes.Remove(_entry.Path);
            _runtime.Release(_pin, _demand);
            Released?.Invoke();
            Released = null;
        }
    }
}
