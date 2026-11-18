using System.Collections;
using System.Text.RegularExpressions;
using AiNative.UnityCore;
using Cysharp.Threading.Tasks;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using AiNative.Examples.PhysicsPlay;
using AiNative.Physics.Jolt;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Examples.PhysicsPlayTests
{
    public sealed class WorldLifetimeTests
    {
        [UnityTest]
        public IEnumerator RejectedSceneLeavesForeignCoreAndInputAlive()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
                !EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload);
            yield return new EnterPlayMode(expectDomainReload: reload);
            var owner = AiNative.UnityCore.Core.Inst;
            yield return owner.RunAsync().ToCoroutine();
            var root = owner.RootGameObject;
            var input = new GameObject("Other application's input").AddComponent<EventSystem>();
            Object.DontDestroyOnLoad(input.gameObject);
            LogAssert.Expect(LogType.Exception, new Regex("Core must be stopped, current state: Running"));
            yield return SceneManager.LoadSceneAsync("PhysicsSandbox");
            yield return null;
            Assert.AreEqual(CoreState.Running, owner.State);
            Assert.AreSame(root, owner.RootGameObject);
            Assert.AreSame(input, EventSystem.current);
            Assert.IsNull(Object.FindFirstObjectByType<SandboxApplication>(FindObjectsInactive.Include));
            Assert.AreEqual(1, Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            Assert.AreEqual(0, JoltWorldFactory.ActiveWorldCount);
            yield return owner.CloseAsync().ToCoroutine();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ExitingPlayReleasesTheOnlyWorld()
        {
            bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
                !EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload);
            EditorSceneManager.OpenScene("Assets/Scenes/PhysicsSandbox.unity");
            Assert.AreEqual(0, JoltWorldFactory.ActiveWorldCount);
            yield return new EnterPlayMode(expectDomainReload: reload);
            Assert.True(Application.isPlaying);
            Assert.NotNull(Object.FindFirstObjectByType<SandboxScene>());
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (JoltWorldFactory.ActiveWorldCount == 0 && EditorApplication.timeSinceStartup < deadline) { yield return null; }
            Assert.AreEqual(1, JoltWorldFactory.ActiveWorldCount);
            yield return new ExitPlayMode();
            Assert.False(EditorApplication.isPlaying);
            Assert.AreEqual(0, JoltWorldFactory.ActiveWorldCount);
        }
    }
}
