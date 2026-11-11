using System;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [CustomEditor(typeof(AssetGroups))]
    internal sealed class AssetGroupsInspector : UnityEditor.Editor
    {
        private string _deploymentResult;
        private BuildTarget _previewTarget = BuildTarget.StandaloneWindows64;
        [MenuItem("AI Native/Assets/Copy latest build to StreamingAssets")]
        private static void CopySelected()
        {
            AssetDeployment.CopyLatestToStreamingAssets(Selection.activeObject as AssetGroups);
        }
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
            AssetBuilder.Build(groups);
        }
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            _previewTarget = (BuildTarget)EditorGUILayout.EnumPopup("Collection preview target", _previewTarget);
            if (GUILayout.Button("Preview platform collection"))
            { AssetPackingWindow.Show(AssetBuildPlan.Create((AssetGroups)target, _previewTarget)); }
            if (GUILayout.Button("Compare build reports")) { AssetAnalysisWindow.OpenComparison(); }
            EditorGUILayout.HelpBox("Editor Load Mode is used by EditorAssets.CreateConfigured at module creation. Restart Play to apply changes. AssetDatabase needs no build; Player always uses bundles.", MessageType.Info);
            var groups = (AssetGroups)target;
            if (GUILayout.Button("Inspect shader candidates and pipeline context"))
            { Debug.Log(JsonUtility.ToJson(AssetShaderCollection.Collect(groups), true)); }
            if (groups.Shaders != null && GUILayout.Button("Generate declared shader variant collection"))
            {
                string path = EditorUtility.SaveFilePanelInProject("New variant collection", "Variants", "shadervariants", "Choose a new asset path; add the result to a resource group.");
                if (!string.IsNullOrEmpty(path)) { AssetShaderCollection.Generate(groups.Shaders, path); }
            }
            EditorGUILayout.LabelField("Last published version", groups.LastBuildVersion.ToString());
            EditorGUILayout.LabelField("Last build", groups.LastBuildPath);
            if (GUILayout.Button("Copy latest build to StreamingAssets"))
            {
                try { _deploymentResult = AssetDeployment.CopyLatestToStreamingAssets(groups).ToString(); }
                catch (Exception error) { _deploymentResult = error.Message; Debug.LogException(error); }
            }
            if (!string.IsNullOrEmpty(_deploymentResult)) { EditorGUILayout.HelpBox(_deploymentResult, MessageType.Info); }
            try { EditorGUILayout.HelpBox("Next output: " + AssetBuildSession.ResolveOutputDirectory((AssetGroups)target), MessageType.Info); }
            catch (Exception error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
            EditorGUILayout.HelpBox("Folders are collected recursively. The global Address Mode applies to every group and asset. Name defaults to Id. Validate to preview addresses and bundle names.", MessageType.Info);
            if (GUILayout.Button("Validate and inspect packing"))
            {
                try { AssetPackingWindow.Show(AssetBuildPlan.Create((AssetGroups)target)); }
                catch (Exception error) { Debug.LogException(error); }
            }
            bool build = GUILayout.Button("Build Windows bundles");
            bool clean = GUILayout.Button("Build Windows bundles without cache");
            if (build || clean)
            {
                try { AssetBuilder.Build((AssetGroups)target, new AssetBuildOptions { UseCache = !clean }); }
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
        private string _search = "";
        private string _state = "";
        private AssetOwnershipSnapshot _baseline;
        public static void Inspect(IAssets assets)
        {
            var window = GetWindow<AssetDiagnosticsWindow>("Asset Ownership");
            window.minSize = new Vector2(700, 400);
            window._assets = assets;
        }
        private void OnGUI()
        {
            if (_assets == null) { EditorGUILayout.LabelField("Attach the explicitly registered IAssets service using Inspect."); return; }
            var state = _assets.GetDiagnostics();
            _search = EditorGUILayout.TextField("Search owners/resources", _search);
            _state = EditorGUILayout.TextField("State filter", _state);
            if (GUILayout.Button("Refresh object memory measurements")) { _assets.RefreshDiagnosticsMemory(); }
            if (GUILayout.Button("Capture baseline")) { _baseline = state.Ownership; }
            if (GUILayout.Button("Export snapshot JSON"))
            {
                string path = EditorUtility.SaveFilePanel("Export ownership", "", "asset-ownership.json", "json");
                if (!string.IsNullOrEmpty(path)) { AssetDiagnosticsExport.Write(state, path); }
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Pending: " + state.PendingOperations);
            if (state.Ownership != null)
            {
                var snapshot = state.Ownership;
                EditorGUILayout.LabelField($"Generation {snapshot.Generation} | active {snapshot.ActiveLoads}, queued {snapshot.QueuedLoads}, peak {snapshot.PeakLoads}");
                EditorGUILayout.LabelField($"Shared hits {snapshot.SharedHits}; failed operations {snapshot.Failures}; cancelled operations {snapshot.Cancellations}");
                if (_baseline != null)
                {
                    EditorGUILayout.HelpBox(AssetDiagnosticsExport.Difference(_baseline, snapshot), MessageType.Info);
                }
                foreach (var owner in snapshot.Owners)
                {
                    string row = $"#{owner.Id} {owner.Kind} {owner.Name} [{owner.State}] owner=#{owner.OwnerId} resource={owner.ResourceKey} bundle={owner.Bundle} dependencies={string.Join(",", owner.Dependencies)}";
                    if (row.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 && owner.State.IndexOf(_state, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        float height = EditorStyles.wordWrappedLabel.CalcHeight(new GUIContent(row), Mathf.Max(200, position.width - 35));
                        EditorGUILayout.SelectableLabel(row, EditorStyles.wordWrappedLabel, GUILayout.Height(height));
                    }
                }
                foreach (var operation in snapshot.RecentOperations)
                {
                    if (operation.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 && operation.State.IndexOf(_state, StringComparison.OrdinalIgnoreCase) >= 0)
                    { EditorGUILayout.LabelField($"#{operation.Id} {operation.Name} {operation.State}, queue={operation.QueueMilliseconds:F2}ms, load={operation.LoadMilliseconds:F2}ms"); }
                }
            }
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
