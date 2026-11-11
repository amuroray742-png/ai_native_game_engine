// Installed into each example's Editor assembly by migrate_examples_urp.py.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class ExampleUrpSetup
{
    private const string Root = "Assets/Rendering";
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("Exit Play before migrating rendering."); }
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i).isDirty) { throw new InvalidOperationException("Save current scene edits before applying URP scene changes."); }
        }
        if (!AssetDatabase.IsValidFolder(Root)) { AssetDatabase.CreateFolder("Assets", "Rendering"); }
        var pipeline = CreatePipeline("Forward", RenderingMode.Forward);
        string project = new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name;
        if (project == "AssetsDemo")
        {
            CreatePipeline("ForwardPlus", RenderingMode.ForwardPlus);
            // Unity 6000.3.23f1 leaks ShaderCompilation TempJob allocations while
            // analyzing URP mesh channels in Builtin Bundle builds. Keep channels;
            // this changes build size, not resource ownership or shader stripping.
            PlayerSettings.stripUnusedMeshComponents = false;
        }
        GraphicsSettings.defaultRenderPipeline = pipeline;
        int previous = QualitySettings.GetQualityLevel();
        for (int quality = 0; quality < QualitySettings.names.Length; quality++)
        {
            QualitySettings.SetQualityLevel(quality, false);
            QualitySettings.renderPipeline = pipeline;
        }
        QualitySettings.SetQualityLevel(previous, false);
        var global = EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>();
        if (global == null)
        {
            var globalType = TypeCache.GetTypesDerivedFrom<RenderPipelineGlobalSettings>()
                .Single(x => x.Name == "UniversalRenderPipelineGlobalSettings");
            global = RenderPipelineGlobalSettingsUtils.Create(globalType, Root + "/GlobalSettings.asset");
            EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>(global);
        }

        Material display = null;
        if (project == "GridGame") { display = MaterialAt(Root + "/GridSprite.mat", "AI Native/Grid Sprite"); }
        if (project == "PhysicsSandbox") { display = MaterialAt(Root + "/Body.mat", "Universal Render Pipeline/Lit"); }
        if (project == "AssetsDemo")
        {
            var shared = AssetDatabase.LoadAssetAtPath<Material>("Assets/Probe/SharedMaterial.mat");
            var texture = shared.mainTexture;
            var scale = shared.mainTextureScale;
            var offset = shared.mainTextureOffset;
            shared.shader = Shader.Find("Universal Render Pipeline/Unlit");
            shared.SetTexture("_BaseMap", texture);
            shared.SetTextureScale("_BaseMap", scale);
            shared.SetTextureOffset("_BaseMap", offset);
            EditorUtility.SetDirty(shared);
            AssetDatabase.SaveAssetIfDirty(shared);
        }
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                    { var data = camera.GetUniversalAdditionalCameraData(); data.renderPostProcessing = false; }
                    foreach (var light in root.GetComponentsInChildren<Light>(true)) { light.GetUniversalAdditionalLightData(); }
                    foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null || display == null) { continue; }
                        var serialized = new SerializedObject(component);
                        string field = component.GetType().Name == "GridBoard" ? "spriteMaterial" : component.GetType().Name == "SandboxScene" ? "bodyMaterial" : null;
                        if (field == null) { continue; }
                        serialized.FindProperty(field).objectReferenceValue = display;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
        }
        finally
        {
            if (setup.Any(x => x.isLoaded) && setup.Any(x => x.isActive)) { EditorSceneManager.RestoreSceneManagerSetup(setup); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("URP migration complete: " + project + ", URP " + UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UniversalRenderPipeline).Assembly).version);
    }

    private static UniversalRenderPipelineAsset CreatePipeline(string name, RenderingMode mode)
    {
        string path = Root + "/" + name + ".asset";
        var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (existing != null) { return existing; }
        var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
        renderer.renderingMode = mode;
        AssetDatabase.CreateAsset(renderer, Root + "/" + name + "Renderer.asset");
        var pipeline = UniversalRenderPipelineAsset.Create(renderer);
        pipeline.supportsHDR = false;
        pipeline.msaaSampleCount = 1;
        var serialized = new SerializedObject(pipeline);
        serialized.FindProperty("m_MainLightShadowsSupported").boolValue = true;
        serialized.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
        serialized.FindProperty("m_AdditionalLightsRenderingMode").intValue = (int)LightRenderingMode.PerPixel;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.CreateAsset(pipeline, path);
        return pipeline;
    }
    private static Material MaterialAt(string path, string shaderName)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) { return material; }
        var shader = Shader.Find(shaderName);
        if (shader == null) { throw new InvalidOperationException("Missing shader: " + shaderName); }
        material = new Material(shader);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
    public static void Forward() { Select("Forward"); }
    public static void ForwardPlus() { Select("ForwardPlus"); }
    private static void Select(string name)
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root + "/" + name + ".asset");
        if (pipeline == null) { throw new InvalidOperationException("Missing pipeline: " + name); }
        GraphicsSettings.defaultRenderPipeline = pipeline;
        int previous = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = pipeline; }
        QualitySettings.SetQualityLevel(previous, false);
        AssetDatabase.SaveAssets();
    }
}
