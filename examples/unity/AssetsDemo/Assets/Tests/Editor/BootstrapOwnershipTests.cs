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
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(expectDomainReload: reload);
            yield return UniTask.ToCoroutine(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var host = await DemoControlsLifecycle.DisableDuringLoad<AssetsDemoBootstrap>("Assets/Bootstrap.unity", timeout.Token);
                Button button = null;
                await UniTask.WaitUntil(() =>
                {
                    button = DemoControlsLifecycle.ButtonIncludingInactive("Run cleanup checks");
                    return button != null && button.GetComponentInParent<DemoControlsView>(true)
                        .GetComponentsInChildren<Text>(true).Any(label => label.text.Contains("share one material"));
                }, cancellationToken: timeout.Token);
                Assert.IsFalse(button.gameObject.activeInHierarchy);
                button.onClick.Invoke();
                host.enabled = true;
                Assert.IsTrue(button.IsInteractable());
                host.enabled = false;
                button.onClick.Invoke();
                await UniTask.NextFrame();
                host.enabled = true;
                Assert.IsTrue(button.IsInteractable(), "A stale click started cleanup while the host was disabled.");
                button.onClick.Invoke();
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
