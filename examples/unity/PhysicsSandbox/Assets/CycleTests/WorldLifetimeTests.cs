using System.Collections;
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
