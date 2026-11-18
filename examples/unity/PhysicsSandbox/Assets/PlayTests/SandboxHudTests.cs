using System.Collections;
using System.Collections.Generic;
using AiNative.Examples.PhysicsPlay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AiNative.Examples.PhysicsPlayTests
{
    public sealed class SandboxHudTests
    {
        internal static void Click(string name)
        {
            var button = GameObject.Find(name).GetComponent<Button>();
            var rect = (RectTransform)button.transform;
            var pointer = new PointerEventData(EventSystem.current) {
                position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)),
                button = PointerEventData.InputButton.Left
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            Assert.IsNotEmpty(hits);
            Assert.AreSame(button.gameObject, ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject));
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }

        internal static IEnumerator WaitForWorld(SandboxScene scene)
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!scene.Ready && scene.StartupError == null && Time.realtimeSinceStartup < deadline) { yield return null; }
            Assert.IsNull(scene.StartupError);
            Assert.True(scene.Ready, "Timed out waiting for bundled presentation");
        }

        [UnityTest]
        public IEnumerator SceneResourcesAndCommandsAreRevokedAcrossRapidReconnect()
        {
            yield return SceneManager.LoadSceneAsync("PhysicsSandbox");
            var scene = Object.FindFirstObjectByType<SandboxScene>();
            scene.FrameSeconds = () => 0;
            yield return WaitForWorld(scene);
            var app = scene.ApplicationHost;
            var body = scene.Display.Body(2);
            Assert.IsNull(body.GetComponent<Collider>());
            Assert.IsNull(body.GetComponent<Rigidbody>());
            Assert.Greater(app.Diagnostics.Bundles.Count, 0);
            var click = GameObject.Find("Spawn sphere").GetComponent<Button>().onClick;
            scene.enabled = false;
            scene.enabled = true;
            scene.enabled = false;
            scene.enabled = true;
            yield return WaitForWorld(scene);
            click.Invoke();
            Assert.AreEqual(0, scene.Session.Pending);
            Assert.AreEqual(1, Object.FindObjectsByType<SandboxHud>(FindObjectsSortMode.None).Length);
            Assert.AreEqual(1, Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length);
            scene.enabled = false;
            float deadline = Time.realtimeSinceStartup + 30;
            while (!app.Idle && Time.realtimeSinceStartup < deadline) { yield return null; }
            Assert.True(app.Idle);
            var diagnostics = app.Diagnostics;
            Assert.Zero(diagnostics.Bundles.Count);
            Assert.Zero(diagnostics.Resources.Count);
            Assert.Zero(diagnostics.PendingOperations);
            Assert.Zero(diagnostics.Errors.Count);
        }

        [UnityTest]
        public IEnumerator GameViewPointerQueuesInputWhilePaused()
        {
            yield return SceneManager.LoadSceneAsync("PhysicsSandbox");
            var scene = Object.FindFirstObjectByType<SandboxScene>();
            scene.FrameSeconds = () => 0;
            yield return WaitForWorld(scene);
            scene.Restart();
            yield return null;
            Click("Pause");
            Assert.True(scene.Paused);
            Click("Spawn sphere");
            yield return null;
            Assert.AreEqual(1, scene.Session.Pending);
            Assert.AreEqual(0, scene.Session.Tick);
            Click("Pause");
            Assert.False(scene.Paused);
            double time = 1d / 60;
            scene.FrameSeconds = () => { double delta = time; time = 0; return delta; };
            yield return null;
            Assert.AreEqual(1, scene.Session.Tick);
            Assert.AreEqual(3, scene.Display.Count);
        }
    }
}
