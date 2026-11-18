using System.Linq;
using System.Threading;
using AiNative.UnityCore;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

internal static class DemoControlsLifecycle
{
    internal static async UniTask<T> DisableDuringLoad<T>(string scene, CancellationToken token) where T : MonoBehaviour
    {
        EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
        await UniTask.WaitUntil(() => Core.Inst.IsRunning, cancellationToken: token);
        var host = Object.FindFirstObjectByType<T>();
        Assert.NotNull(host);
        Assert.IsNull(Object.FindFirstObjectByType<DemoControlsView>(), "Expected the real HUD bundle to be loading.");
        host.enabled = false;
        return host;
    }

    internal static Button ButtonIncludingInactive(string name) =>
        Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .SingleOrDefault(button => button.name == name);
}
