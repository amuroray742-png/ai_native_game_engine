using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.UnityCore.Assets
{
    internal static class SceneLoadOperation
    {
        internal static async UniTask<Scene> LoadAsync(string path, Func<AsyncOperation> start)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (string.Equals(SceneManager.GetSceneAt(i).path, path, StringComparison.OrdinalIgnoreCase))
                { throw new AssetException("SceneAlreadyLoaded", "A scene with this path is already loaded outside this request.", path); }
            }
            Scene loaded = default;
            void OnLoaded(Scene scene, LoadSceneMode mode)
            {
                if (string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase)) { loaded = scene; }
            }
            SceneManager.sceneLoaded += OnLoaded;
            try
            {
                await start();
                if (!loaded.IsValid()) { throw new AssetException("SceneLoadFailed", "No sceneLoaded identity was received.", path); }
                return loaded;
            }
            finally { SceneManager.sceneLoaded -= OnLoaded; }
        }
    }
}
