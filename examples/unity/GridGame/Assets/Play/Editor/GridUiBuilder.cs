using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AiNative.Examples.GridGame.Play.Editor
{
    public static class GridUiBuilder
    {
        [MenuItem("GridGame/Migrate scene HUD to UI module")]
        public static void Migrate()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/GridGame.unity");
            var legacy = UnityEngine.Object.FindFirstObjectByType<GridHud>();
            if (legacy != null)
            {
                Directory.CreateDirectory("Assets/UI");
                AssetDatabase.Refresh();
                var root = legacy.gameObject;
                root.name = "GridHud";
                var children = root.transform.Cast<Transform>().ToArray();
                UnityEngine.Object.DestroyImmediate(root.GetComponent<GraphicRaycaster>());
                UnityEngine.Object.DestroyImmediate(root.GetComponent<CanvasScaler>());
                UnityEngine.Object.DestroyImmediate(root.GetComponent<Canvas>());
                var group = root.AddComponent<CanvasGroup>();
                Stretch((RectTransform)root.transform);
                var safe = Container("SafeArea", root.transform);
                var visual = Container("Visual", safe);
                foreach (var child in children) { child.SetParent(visual, false); }
                var view = new SerializedObject(legacy);
                view.FindProperty("_canvasGroup").objectReferenceValue = group;
                view.FindProperty("_safeArea").objectReferenceValue = safe;
                view.FindProperty("_visual").objectReferenceValue = visual;
                view.FindProperty("_defaultSelection").objectReferenceValue = view.FindProperty("endTurn").objectReferenceValue;
                view.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/UI/GridHud.prefab");
                UnityEngine.Object.DestroyImmediate(root);
            }
            foreach (var events in UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
            { UnityEngine.Object.DestroyImmediate(events.gameObject); }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/UI/GridHud.prefab");
            if (prefab == null) { throw new InvalidOperationException("Grid HUD prefab is missing."); }
            var content = PrefabUtility.LoadPrefabContents("Assets/UI/GridHud.prefab");
            try
            {
                foreach (var label in content.GetComponentsInChildren<Text>(true))
                {
                    if (label.name == "Label") { label.name = label.transform.parent.name + " label"; }
                }
                PrefabUtility.SaveAsPrefabAsset(content, "Assets/UI/GridHud.prefab");
            }
            finally { PrefabUtility.UnloadPrefabContents(content); }
            var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/UI/ResourceGroups.asset");
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<AssetGroups>();
                AssetDatabase.CreateAsset(settings, "Assets/UI/ResourceGroups.asset");
            }
            settings.Groups = new[] { new ResourceGroup { Id = "GridHud", Assets = new[] { new ResourceAddress { Asset = prefab } } } };
            settings.EditorLoadMode = EditorAssetLoadMode.AssetBundle;
            settings.BuildBackend = AssetBuildBackend.ScriptableBuildPipeline;
            EditorUtility.SetDirty(settings);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        [MenuItem("GridGame/Build and deploy UI content")]
        public static void BuildContent()
        {
            var original = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/UI/ResourceGroups.asset");
            if (original == null) { throw new InvalidOperationException("Run GridGame/Migrate scene HUD to UI module first."); }
            var settings = UnityEngine.Object.Instantiate(original);
            try
            {
                string output = Path.GetFullPath("BuildArtifacts/GridUI/" + Guid.NewGuid().ToString("N"));
                AssetBuilder.Build(settings, output);
                AssetDeployment.CopyLatestToStreamingAssets(settings);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
            AssetDatabase.Refresh();
        }

        private static RectTransform Container(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Stretch(rect);
            return rect;
        }
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
        }
    }
}
