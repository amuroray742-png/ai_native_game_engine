using System.Collections;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.Assets.Editor;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UIPlayCycleTests
    {
        private const string Folder = "Assets/UIPlayCycleFixture";
        private const string Address = Folder + "/Window";
        private const string UnboundKey = "AiNative.UI.Tests.PlayCycle.Unbound";
        private const string SuspendedKey = "AiNative.UI.Tests.PlayCycle.Suspended";

        private static void PrepareFixture()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.CreateFolder("Assets", "UIPlayCycleFixture");
            var source = new GameObject("Window", typeof(RectTransform), typeof(CanvasGroup), typeof(UIView));
            source.SetActive(false);
            var safe = new GameObject("SafeArea", typeof(RectTransform));
            safe.transform.SetParent(source.transform, false);
            var visual = new GameObject("Visual", typeof(RectTransform));
            visual.transform.SetParent(safe.transform, false);
            var serialized = new SerializedObject(source.GetComponent<UIView>());
            serialized.FindProperty("_canvasGroup").objectReferenceValue = source.GetComponent<CanvasGroup>();
            serialized.FindProperty("_safeArea").objectReferenceValue = safe.transform;
            serialized.FindProperty("_visual").objectReferenceValue = visual.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(source, Address + ".prefab");
            Object.DestroyImmediate(source);
            SessionState.SetInt(UnboundKey, 0);
            SessionState.SetInt(SuspendedKey, 0);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) { yield return new ExitPlayMode(); }
            AssetDatabase.DeleteAsset(Folder);
        }

        [UnityTest]
        public IEnumerator TwoRealPlayExitsUnbindUiBeforeNextSession()
        {
            PrepareFixture();
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            yield return OpenWindowAsync().ToCoroutine();
            yield return new ExitPlayMode();
            Assert.AreEqual(1, SessionState.GetInt(UnboundKey, -1));
            Assert.AreEqual(1, SessionState.GetInt(SuspendedKey, -1));
            Assert.IsNull(GameObject.Find("UnityCore UI"));

            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            Assert.AreEqual(CoreState.Stopped, Core.Inst.State);
            yield return OpenWindowAsync().ToCoroutine();
            yield return new ExitPlayMode();
            Assert.AreEqual(2, SessionState.GetInt(UnboundKey, -1));
            Assert.AreEqual(2, SessionState.GetInt(SuspendedKey, -1));
            Assert.IsNull(GameObject.Find("UnityCore UI"));
        }

        private static async UniTask OpenWindowAsync()
        {
            var groups = ScriptableObject.CreateInstance<AssetGroups>();
            groups.EditorLoadMode = EditorAssetLoadMode.AssetDatabase;
            groups.Groups = new[] { new ResourceGroup { Id = "ui-cycle", Assets = new[]
            {
                new ResourceAddress { Asset = AssetDatabase.LoadAssetAtPath<GameObject>(Address + ".prefab") }
            } } };
            var assets = EditorAssets.CreateConfigured(groups);
            var events = new GameObject("UI Cycle EventSystem", typeof(EventSystem));
            var ui = new UIModule(new UIOptions(), events.GetComponent<EventSystem>());
            ui.Register(new UIWindowDefinition<UIView>(Address, UIRole.Hud, (view, scope) => new CyclePresenter()));
            Core.Inst.RegisterModule(assets).RegisterModule(ui);
            await Core.Inst.RunAsync();
            Assert.IsTrue((await ui.OpenAsync<UIView>()).Succeeded);
            Object.Destroy(groups);
        }

        private static bool ReloadEnabled() => !EditorSettings.enterPlayModeOptionsEnabled ||
            !EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload);

        public static void ReloadOn() { SetReload(EnterPlayModeOptions.None); }
        public static void ReloadOff() { SetReload(EnterPlayModeOptions.DisableDomainReload); }
        private static void SetReload(EnterPlayModeOptions options)
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = options;
            AssetDatabase.SaveAssets();
        }

        private sealed class CyclePresenter : UIPresenter
        {
            public override void Bind() { }
            public override void Resume() { }
            public override void Suspend() { SessionState.SetInt(SuspendedKey, SessionState.GetInt(SuspendedKey, 0) + 1); }
            public override void Unbind() { SessionState.SetInt(UnboundKey, SessionState.GetInt(UnboundKey, 0) + 1); }
        }
    }
}
