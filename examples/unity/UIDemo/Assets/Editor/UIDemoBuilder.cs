using System;
using System.Collections.Generic;
using System.IO;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AiNative.UIDemo.Editor
{
    public static class UIDemoBuilder
    {
        private static Font Font => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        public static void ReloadOn() { SetReload(false); }
        public static void ReloadOff() { SetReload(true); }
        private static void SetReload(bool disabled)
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = disabled ? EnterPlayModeOptions.DisableDomainReload : EnterPlayModeOptions.None;
            AssetDatabase.SaveAssets();
        }

        [MenuItem("UIDemo/Create scenes and content")]
        public static void Create()
        {
            Directory.CreateDirectory("Assets/Content");
            AssetDatabase.Refresh();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Content/SharedImage.asset");
            if (texture == null)
            {
                texture = new Texture2D(64, 64) { name = "SharedImage", filterMode = FilterMode.Bilinear };
                var pixels = new Color[64 * 64];
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    { pixels[y * 64 + x] = Color.Lerp(new Color(0.1f, 0.85f, 0.72f), new Color(0.25f, 0.3f, 0.9f), (x + y) / 126f); }
                }
                texture.SetPixels(pixels); texture.Apply();
                AssetDatabase.CreateAsset(texture, "Assets/Content/SharedImage.asset");
            }
            var groups = new List<ResourceGroup>();
            void Add<T>(string title, Color color, params DemoAction[] actions) where T : DemoView
            {
                var prefab = MakeWindow<T>(title, color, texture, actions);
                groups.Add(new ResourceGroup { Id = typeof(T).Name, Assets = new[] { new ResourceAddress { Asset = prefab } } });
            }
            Add<HomeView>("01 / HOME", new Color(0.055f, 0.09f, 0.16f), DemoAction.Next, DemoAction.Modal, DemoAction.Toast, DemoAction.Back, DemoAction.Duplicate, DemoAction.CancelTransition);
            Add<GalleryView>("02 / GALLERY", new Color(0.08f, 0.16f, 0.22f), DemoAction.Third, DemoAction.Transparent, DemoAction.Modal, DemoAction.Back);
            Add<DetailsView>("03 / DETAILS", new Color(0.15f, 0.1f, 0.24f), DemoAction.Transparent, DemoAction.Modal, DemoAction.Back);
            Add<TransparentView>("04 / TRANSPARENT PAGE", new Color(0.17f, 0.25f, 0.29f, 0.9f), DemoAction.Back, DemoAction.Modal);
            Add<FirstModalView>("MODAL / FIRST", new Color(0.12f, 0.2f, 0.28f), DemoAction.SecondModal, DemoAction.Back);
            Add<SecondModalView>("MODAL / SECOND", new Color(0.22f, 0.15f, 0.3f), DemoAction.Back);
            Add<HudView>("HUD / PERSISTENT", new Color(0.1f, 0.25f, 0.26f), DemoAction.Toast, DemoAction.Hud);
            Add<ToastView>("TOAST / INPUT PASSES THROUGH", new Color(0.1f, 0.45f, 0.38f));
            groups.Add(new ResourceGroup { Id = "Image", Assets = new[] { new ResourceAddress { Asset = texture } } });
            var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<AssetGroups>();
                AssetDatabase.CreateAsset(settings, "Assets/ResourceGroups.asset");
            }
            settings.Groups = groups.ToArray();
            settings.EditorLoadMode = EditorAssetLoadMode.AssetBundle;
            settings.BuildBackend = AssetBuildBackend.ScriptableBuildPipeline;
            EditorUtility.SetDirty(settings);
            CreateScenes();
            PlayerSettings.runInBackground = true;
            PlayerSettings.stripUnusedMeshComponents = false;
            AssetDatabase.SaveAssets();
        }

        private static GameObject MakeWindow<T>(string title, Color color, Texture2D texture, DemoAction[] actions) where T : DemoView
        {
            var root = Rect(typeof(T).Name, null, Vector2.zero, new Vector2(1920, 1080));
            Stretch(root);
            var view = root.gameObject.AddComponent<T>();
            var group = root.gameObject.AddComponent<CanvasGroup>();
            var safe = Rect("SafeArea", root, Vector2.zero, Vector2.zero); Stretch(safe);
            var visual = Rect("Visual", safe, Vector2.zero, Vector2.zero); Stretch(visual);
            visual.gameObject.AddComponent<CanvasGroup>();
            bool hud = typeof(T) == typeof(HudView), toast = typeof(T) == typeof(ToastView);
            bool modal = typeof(T) == typeof(FirstModalView) || typeof(T) == typeof(SecondModalView);
            bool transparent = typeof(T) == typeof(TransparentView);
            var size = hud ? new Vector2(1740, 110) : toast ? new Vector2(720, 85) : modal ? new Vector2(880, 430) : transparent ? new Vector2(1020, 570) : new Vector2(1920, 850);
            var center = hud ? new Vector2(0, -460) : toast ? new Vector2(510, 340) : new Vector2(0, -30);
            var panel = Rect("Panel", visual, center, size);
            bool page = !hud && !toast && !modal && !transparent;
            if (page)
            {
                Stretch(panel); panel.offsetMin = new Vector2(0, 140); panel.offsetMax = new Vector2(0, -190);
            }
            if (hud) { panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0); panel.anchoredPosition = new Vector2(0, 70); }
            panel.gameObject.AddComponent<Image>().color = color;
            view.Caption = Label("Caption", panel, title, new Vector2(-size.x / 2 + 40, size.y / 2 - 55), new Vector2(size.x - 80, 60), hud || toast ? 25 : 40);
            if (page) { TopLeft(view.Caption.rectTransform, new Vector2(40 + (size.x - 80) / 2, -55)); }
            view.Picture = Rect("Shared image", panel, new Vector2(size.x / 2 - 90, 0), new Vector2(hud || toast ? 50 : 100, hud || toast ? 50 : 100)).gameObject.AddComponent<RawImage>();
            view.Picture.texture = texture;
            view.PrefabImage = texture;
            view.Picture.raycastTarget = false;
            if (page) { view.Picture.rectTransform.anchorMin = view.Picture.rectTransform.anchorMax = new Vector2(1, 0.5f); view.Picture.rectTransform.anchoredPosition = new Vector2(-90, 0); }
            var commands = new List<DemoCommand>();
            for (int i = 0; i < actions.Length; i++)
            {
                var position = hud ? new Vector2(300 + i * 240, 0) : new Vector2(-size.x / 2 + 160 + (i % 3) * 270, size.y / 2 - 170 - (i / 3) * 90);
                var command = Button(panel, actions[i], position);
                if (page) { TopLeft(command.GetComponent<RectTransform>(), new Vector2(160 + (i % 3) * 270, -170 - (i / 3) * 90)); }
                commands.Add(command);
            }
            view.Commands = commands.ToArray();
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_canvasGroup").objectReferenceValue = group;
            serialized.FindProperty("_safeArea").objectReferenceValue = safe;
            serialized.FindProperty("_visual").objectReferenceValue = visual;
            if (commands.Count > 0) { serialized.FindProperty("_defaultSelection").objectReferenceValue = commands[0].Button; }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(root.gameObject, "Assets/Content/" + typeof(T).Name + ".prefab");
            UnityEngine.Object.DestroyImmediate(root.gameObject);
            return prefab;
        }

        private static void CreateScenes()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var host = new GameObject("UIDemo Host").AddComponent<UIDemoBootstrap>();
            host.Events = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule)).GetComponent<EventSystem>();
            host.Events.transform.SetParent(host.transform);
            var canvas = Rect("Host controls", host.transform, Vector2.zero, Vector2.zero);
            canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<Canvas>().sortingOrder = 100;
            var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            canvas.gameObject.AddComponent<GraphicRaycaster>();
            var panel = Rect("Dashboard", canvas, new Vector2(0, 445), new Vector2(1920, 190));
            panel.anchorMin = new Vector2(0, 1); panel.anchorMax = Vector2.one;
            panel.pivot = new Vector2(0.5f, 1); panel.anchoredPosition = Vector2.zero; panel.sizeDelta = new Vector2(0, 190);
            panel.gameObject.AddComponent<Image>().color = new Color(0.025f, 0.04f, 0.07f);
            var title = Label("Title", panel, "UNITYCORE / UI LAB", new Vector2(-920, 65), new Vector2(760, 50), 30);
            host.Status = Label("Status", panel, "Starting", new Vector2(-140, 65), new Vector2(1050, 50), 24);
            host.Diagnostics = Label("Diagnostics", panel, "", new Vector2(-920, 20), new Vector2(1840, 40), 22);
            TopLeft(title.rectTransform, new Vector2(420, -30));
            TopLeft(host.Status.rectTransform, new Vector2(1305, -30));
            TopLeft(host.Diagnostics.rectTransform, new Vector2(960, -75));
            var actions = new[] { DemoAction.Clear, DemoAction.FaultClear, DemoAction.Trim, DemoAction.Scene, DemoAction.Hud };
            var commands = new List<DemoCommand>();
            for (int i = 0; i < actions.Length; i++)
            {
                var command = Button(panel, actions[i], Vector2.zero);
                TopLeft(command.GetComponent<RectTransform>(), new Vector2(180 + i * 300, -145));
                commands.Add(command);
            }
            host.Commands = commands.ToArray();
            EditorSceneManager.SaveScene(scene, "Assets/Bootstrap.unity");
            var other = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Other scene marker");
            EditorSceneManager.SaveScene(other, "Assets/Other.unity");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Bootstrap.unity", true), new EditorBuildSettingsScene("Assets/Other.unity", true) };
            EditorSceneManager.OpenScene("Assets/Bootstrap.unity");
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position; rect.sizeDelta = size;
            return rect;
        }
        private static void Stretch(RectTransform rect)
        { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private static void TopLeft(RectTransform rect, Vector2 position)
        { rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.anchoredPosition = position; }
        private static Text Label(string name, Transform parent, string value, Vector2 leftCenter, Vector2 size, int fontSize)
        {
            var rect = Rect(name, parent, leftCenter + new Vector2(size.x / 2, 0), size);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = Font; text.fontSize = fontSize; text.text = value; text.color = new Color(0.9f, 0.96f, 1);
            text.alignment = TextAnchor.MiddleLeft; text.raycastTarget = false;
            return text;
        }
        private static DemoCommand Button(Transform parent, DemoAction action, Vector2 center)
        {
            var rect = Rect(action.ToString(), parent, center, new Vector2(240, 58));
            rect.gameObject.AddComponent<Image>().color = new Color(0.14f, 0.4f, 0.46f);
            var button = rect.gameObject.AddComponent<Button>();
            var text = Label(action + " Label", rect, action.ToString(), new Vector2(-115, 0), new Vector2(230, 50), 25);
            text.alignment = TextAnchor.MiddleCenter;
            var command = rect.gameObject.AddComponent<DemoCommand>(); command.Action = action; command.Button = button;
            return command;
        }

        [MenuItem("UIDemo/Build and deploy SBP content")]
        public static void BuildContentSbp()
        {
            var original = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            var settings = UnityEngine.Object.Instantiate(original);
            int version = original.Version;
            try
            {
                settings.BuildBackend = AssetBuildBackend.ScriptableBuildPipeline;
                string output = Path.GetFullPath("BuildArtifacts/UIContent/" + Guid.NewGuid().ToString("N"));
                AssetBuilder.Build(settings, output);
                AssetDeployment.CopyLatestToStreamingAssets(settings);
                if (original.Version != version) { throw new InvalidOperationException("Development version changed."); }
                Debug.Log("UIDemo content published: " + output);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
            AssetDatabase.Refresh();
        }
    }
}
