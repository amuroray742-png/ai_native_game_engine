using System;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

internal static class DemoControlsSetup
{
    internal const string AssetsPanel = "Assets/DemoControls/AssetsPanel.prefab";
    internal const string ShaderPanel = "Assets/DemoControls/ShaderPanel.prefab";

    internal static ResourceGroup Group(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) { throw new InvalidOperationException("Missing demo controls prefab: " + path); }
        return new ResourceGroup { Id = "Controls", Assets = new[] { new ResourceAddress { Asset = prefab } } };
    }

    // This scene-owned label displays startup and shutdown results while the UI module is offline.
    internal static void AddStatus(MonoBehaviour host)
    {
        var canvas = Child("Demo result", host.transform);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(900, 650);
        scaler.matchWidthOrHeight = .5f;
        var label = Label("Status", canvas, "Starting…");
        Stretch(label.rectTransform);
        label.rectTransform.offsetMin = new Vector2(20, 12);
        label.rectTransform.offsetMax = new Vector2(-20, -12);
        var serialized = new SerializedObject(host);
        serialized.FindProperty("sceneStatus").objectReferenceValue = label;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static RectTransform Child(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    private static Text Label(string name, Transform parent, string text)
    {
        var label = Child(name, parent).gameObject.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 18;
        label.text = text;
        label.raycastTarget = false;
        return label;
    }

    private static void CreatePanel(string path, params string[] captions)
    {
        var root = Child("Panel", null);
        try
        {
            var view = root.gameObject.AddComponent<DemoControlsView>();
            var group = root.gameObject.AddComponent<CanvasGroup>();
            Stretch(root);
            var safe = Child("SafeArea", root);
            Stretch(safe);
            var visual = Child("Visual", safe);
            Stretch(visual);
            var label = Label("Status", visual, "");
            var rect = label.rectTransform;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(20, -94);
            rect.offsetMax = new Vector2(-20, -12);
            var buttons = new Button[captions.Length];
            for (int i = 0; i < captions.Length; i++)
            {
                var buttonRect = Child(captions[i], visual);
                buttonRect.pivot = buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0, 1);
                buttonRect.anchoredPosition = new Vector2(20 + 210 * i, -98);
                buttonRect.sizeDelta = new Vector2(200, 40);
                var background = buttonRect.gameObject.AddComponent<Image>();
                background.color = new Color(.16f, .24f, .32f);
                var button = buttonRect.gameObject.AddComponent<Button>();
                button.targetGraphic = background;
                var caption = Label(captions[i] + " label", buttonRect, captions[i]);
                caption.alignment = TextAnchor.MiddleCenter;
                caption.fontSize = 16;
                Stretch(caption.rectTransform);
                buttons[i] = button;
            }
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_canvasGroup").objectReferenceValue = group;
            serialized.FindProperty("_safeArea").objectReferenceValue = safe;
            serialized.FindProperty("_visual").objectReferenceValue = visual;
            serialized.FindProperty("_defaultSelection").objectReferenceValue = buttons[0];
            serialized.FindProperty("status").objectReferenceValue = label;
            var items = serialized.FindProperty("buttons");
            items.arraySize = buttons.Length;
            for (int i = 0; i < buttons.Length; i++) { items.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i]; }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root.gameObject, path);
        }
        finally { Object.DestroyImmediate(root.gameObject); }
    }

    public static void CreateMissingControls()
    {
        if (!AssetDatabase.IsValidFolder("Assets/DemoControls")) { AssetDatabase.CreateFolder("Assets", "DemoControls"); }
        if (AssetDatabase.LoadAssetAtPath<GameObject>(AssetsPanel) == null)
        { CreatePanel(AssetsPanel, "Run cleanup checks"); }
        if (AssetDatabase.LoadAssetAtPath<GameObject>(ShaderPanel) == null)
        { CreatePanel(ShaderPanel, "Base variant (red)", "PROBE_GREEN", "Close demo"); }
    }
}
