using System.Collections;
using System;
using System.Linq;
using System.Threading;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using System.Text.RegularExpressions;
using AiNative.UnityCore;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.AssetsDemoTests
{
    public sealed class BootstrapOwnershipTests
    {
        [UnityTest]
        public IEnumerator RejectedBootstrapLeavesRunningCoreAlive()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(expectDomainReload: reload);
            var owner = Core.Inst;
            yield return owner.RunAsync().ToCoroutine();
            var root = owner.RootGameObject;
            LogAssert.Expect(LogType.Exception, new Regex("Core must be stopped, current state: Running"));
            var duplicate = new GameObject("Rejected example host");
            duplicate.AddComponent<AssetsDemoBootstrap>();
            yield return null;
            Object.Destroy(duplicate);
            yield return null;
            Assert.AreEqual(CoreState.Running, owner.State);
            Assert.AreSame(root, owner.RootGameObject);
            yield return owner.CloseAsync().ToCoroutine();
        }

        [UnityTest]
        public IEnumerator CleanupButtonReleasesResourcesAndShowsResult()
        {
            EditorSceneManager.OpenScene("Assets/Bootstrap.unity");
            bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(expectDomainReload: reload);
            yield return UniTask.ToCoroutine(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var host = Object.FindFirstObjectByType<AssetsDemoBootstrap>();
                await UniTask.WaitUntil(() => GameObject.Find("Run cleanup checks")?.GetComponent<Button>().IsInteractable() == true,
                    cancellationToken: timeout.Token);
                GameObject.Find("Run cleanup checks").GetComponent<Button>().onClick.Invoke();
                await UniTask.WaitUntil(() => host.GetComponentsInChildren<Text>().Any(label =>
                    label.isActiveAndEnabled && label.text.StartsWith("PASS:", StringComparison.Ordinal)),
                    cancellationToken: timeout.Token);
                Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles());
            });
        }

        [UnityTearDown]
        public IEnumerator ExitPlay()
        {
            if (EditorApplication.isPlaying) { yield return new ExitPlayMode(); }
        }
    }
}
