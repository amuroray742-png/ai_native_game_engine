using System;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [CustomEditor(typeof(AssetGroups))]
    internal sealed class AssetGroupsInspector : UnityEditor.Editor
    {
        [MenuItem("AI Native/Assets/Validate selected groups")]
        private static void ValidateSelected()
        {
            AssetPackingWindow.Show(AssetBuildPlan.Create(Selection.activeObject as AssetGroups));
        }
        [MenuItem("AI Native/Assets/Build selected groups")]
        private static void BuildSelected()
        {
            var groups = Selection.activeObject as AssetGroups;
            if (groups == null) { throw new InvalidOperationException("Select an AssetGroups configuration first."); }
            string path = EditorUtility.SaveFilePanel("New build directory", "", "AssetsContent", "");
            if (path.Length != 0) { AssetBuilder.Build(groups, path); }
        }
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Folders are collected recursively. The global Address Mode applies to every group and asset. Name defaults to Id. Validate to preview addresses and bundle names.", MessageType.Info);
            if (GUILayout.Button("Validate and inspect packing"))
            {
                try { AssetPackingWindow.Show(AssetBuildPlan.Create((AssetGroups)target)); }
                catch (Exception error) { Debug.LogException(error); }
            }
            if (GUILayout.Button("Build Windows bundles to new directory"))
            {
                string path = EditorUtility.SaveFilePanel("New build directory", "", "AssetsContent", "");
                if (path.Length == 0) { return; }
                try { AssetBuilder.Build((AssetGroups)target, path); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }
    }
    public sealed class AssetPackingWindow : EditorWindow
    {
        private string _text = "Select a resource groups asset and validate its packing.";
        private Vector2 _scroll;
        internal static void Show(AssetBuildPlan plan)
        {
            var window = GetWindow<AssetPackingWindow>("Asset Packing");
            window._text = "Public addresses\n" + string.Join("\n", System.Linq.Enumerable.Select(plan.Addresses, x => x.Address + " → " + x.Path + " → " + x.Bundle))
                + "\n\nAll resource owners\n" + string.Join("\n", System.Linq.Enumerable.Select(plan.Owners, x => x.Key + " → " + x.Value))
                + "\n\nExcluded sources\n" + string.Join("\n", System.Linq.Enumerable.OrderBy(System.Linq.Enumerable.Distinct(plan.Excluded), x => x, StringComparer.Ordinal));
        }
        private void OnGUI() { _scroll = EditorGUILayout.BeginScrollView(_scroll); EditorGUILayout.TextArea(_text); EditorGUILayout.EndScrollView(); }
    }
    public sealed class AssetDiagnosticsWindow : EditorWindow
    {
        private IAssets _assets;
        private Vector2 _scroll;
        public static void Inspect(IAssets assets) { GetWindow<AssetDiagnosticsWindow>("Asset Ownership")._assets = assets; }
        private void OnGUI()
        {
            if (_assets == null) { EditorGUILayout.LabelField("Attach the explicitly registered IAssets service using Inspect."); return; }
            var state = _assets.GetDiagnostics();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Pending: " + state.PendingOperations);
            foreach (string row in state.Scopes) { EditorGUILayout.LabelField(row); }
            foreach (string row in state.Bundles) { EditorGUILayout.LabelField(row); }
            foreach (string row in state.Resources) { EditorGUILayout.LabelField(row); }
            foreach (string row in state.Pools) { EditorGUILayout.LabelField(row); }
            foreach (string row in state.Errors) { EditorGUILayout.HelpBox(row, MessageType.Error); }
            EditorGUILayout.EndScrollView();
            Repaint();
        }
    }
}
