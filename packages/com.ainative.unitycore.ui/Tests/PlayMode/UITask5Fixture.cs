using System;
using System.Collections;
using System.Collections.Generic;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.Assets.Editor;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI.Tests
{
    public abstract class UITask5Fixture
    {
        protected const string Folder = "Assets/UITask5Fixture";
        protected Core Core;
        protected UIModule UI;
        protected AssetsModule Assets;
        protected EventSystem Events;
        protected readonly Dictionary<Type, UIView> Views = new Dictionary<Type, UIView>();
        protected readonly Dictionary<Type, Probe> Probes = new Dictionary<Type, Probe>();
        private AssetGroups _groups;

        [SetUp]
        public void SetUp()
        {
            AiNative.UnityCore.Core.ResetForPlay();
            Core = AiNative.UnityCore.Core.Inst;
            Views.Clear();
            Probes.Clear();
            NavigationView.Activation = null;
            NavigationSelectionProbe.SelectionChanged = null;
            Time.timeScale = 1;
            AssetDatabase.CreateFolder("Assets", "UITask5Fixture");
            _groups = ScriptableObject.CreateInstance<AssetGroups>();
            _groups.EditorLoadMode = EditorAssetLoadMode.AssetDatabase;
            _groups.Groups = new[] { new ResourceGroup { Id = "task5", Assets = new[] {
                Prefab<FirstPageView>(), Prefab<SecondPageView>(), Prefab<NavigationHudView>(),
                Prefab<FirstModalView>(), Prefab<SecondModalView>() } } };
            Assets = EditorAssets.CreateConfigured(_groups);
            Events = new GameObject("Task5 EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
            UI = new UIModule(new UIOptions(), Events);
        }

        protected async UniTask StartAsync(UITransitionOptions transition = default, bool animateHud = true,
            AiNative.UnityCore.Interfaces.IModule afterUI = null)
        {
            Register<FirstPageView>(UIRole.Page);
            Register<SecondPageView>(UIRole.Page, transition);
            Register<NavigationHudView>(UIRole.Hud, animateHud ? transition : default);
            Register<FirstModalView>(UIRole.Modal, transition);
            Register<SecondModalView>(UIRole.Modal);
            UI.Register(new UIWindowDefinition<UIView>(Folder + "/FirstPageView", UIRole.Overlay,
                (view, scope) => new Probe()));
            Core.RegisterModule(Assets).RegisterModule(UI);
            if (afterUI != null) { Core.RegisterModule(afterUI); }
            await Core.RunAsync();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            Time.timeScale = 1;
            NavigationView.Activation = null;
            NavigationSelectionProbe.SelectionChanged = null;
            foreach (var probe in Probes.Values) { probe.OnResume = probe.OnSuspend = probe.OnUnbind = () => { }; }
            await Core.CloseAsync();
            UnityEngine.Object.DestroyImmediate(Events.gameObject);
            UnityEngine.Object.DestroyImmediate(_groups);
            AssetDatabase.DeleteAsset(Folder);
            AiNative.UnityCore.Core.ResetForPlay();
        });

        private ResourceAddress Prefab<T>() where T : UIView
        {
            var root = Rect(typeof(T).Name, null);
            root.gameObject.SetActive(false);
            var view = root.gameObject.AddComponent<T>();
            var group = root.gameObject.AddComponent<CanvasGroup>();
            var safe = Rect("SafeArea", root);
            var visual = Rect("Visual", safe);
            visual.gameObject.AddComponent<CanvasGroup>().alpha = 0.8f;
            visual.anchoredPosition = new Vector2(3, 4);
            visual.localScale = new Vector3(0.9f, 0.8f, 1);
            var button = Rect("Default", visual);
            button.anchorMin = button.anchorMax = new Vector2(0.5f, 0.5f);
            button.sizeDelta = new Vector2(120, 80);
            button.gameObject.AddComponent<Image>();
            var selection = button.gameObject.AddComponent<Button>();
            button.gameObject.AddComponent<NavigationSelectionProbe>();
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_canvasGroup").objectReferenceValue = group;
            serialized.FindProperty("_safeArea").objectReferenceValue = safe;
            serialized.FindProperty("_visual").objectReferenceValue = visual;
            serialized.FindProperty("_defaultSelection").objectReferenceValue = selection;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(root.gameObject, Folder + "/" + typeof(T).Name + ".prefab");
            UnityEngine.Object.DestroyImmediate(root.gameObject);
            return new ResourceAddress { Asset = prefab };
        }

        protected static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }

        private void Register<T>(UIRole role, UITransitionOptions transition = default) where T : UIView
        {
            var probe = new Probe();
            Probes.Add(typeof(T), probe);
            UI.Register(new UIWindowDefinition<T>(Folder + "/" + typeof(T).Name, role,
                (view, scope) => { Views[typeof(T)] = view; probe.Binds++; return probe; },
                UICachePolicy.Cache, opaqueFullscreen: role == UIRole.Page, transition: transition));
        }

        protected sealed class Probe : UIPresenter
        {
            public int Binds, Resumes, Suspends, Unbinds;
            public Action OnResume = () => { };
            public Action OnSuspend = () => { };
            public Action OnUnbind = () => { };
            public override void Bind() { }
            public override void Resume() { Resumes++; OnResume(); }
            public override void Suspend() { Suspends++; OnSuspend(); }
            public override void Unbind() { Unbinds++; OnUnbind(); }
        }
    }
}
