using System;
using System.Collections;
using AiNative.Examples.GridGame.Play;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Examples.GridGame.PlayTests
{
    public sealed class PlayCycleTests
    {
        [UnityTest]
        public IEnumerator TwoPlayEntriesCreateFreshSessionsAndReleaseOnBothExits()
        {
            Assume.That(EditorSettings.enterPlayModeOptionsEnabled &&
                EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload),
                "This case exercises two entries in the same domain.");
            EditorSceneManager.OpenScene("Assets/Scenes/GridGame.unity");
            for (int cycle = 0; cycle < 2; cycle++)
            {
                yield return new EnterPlayMode(expectDomainReload: false);
                Assert.True(Application.isPlaying);
                var bootstrap = UnityEngine.Object.FindFirstObjectByType<GridGameBootstrap>();
                var session = bootstrap.Session;
                Assert.AreEqual(0, session.Tick);
                bootstrap.Advance();
                Assert.AreEqual(1, session.Tick);
                yield return new ExitPlayMode();
                Assert.False(EditorApplication.isPlaying);
                Assert.Throws<ObjectDisposedException>(() => session.CaptureObservation());
            }
        }
    }
}
