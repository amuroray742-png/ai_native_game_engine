using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class ShaderVariantDemoSetup
{
    public const string Root = "Assets/ShaderVariantDemo";
    public const string ScenePath = Root + "/Demo.unity";
    public const string GroupsPath = Root + "/Groups.asset";

    [MenuItem("AI Native/Shader Variant Demo/Create missing assets")]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("Exit Play first."); }
        if (!AssetDatabase.IsValidFolder(Root)) { AssetDatabase.CreateFolder("Assets", "ShaderVariantDemo"); }
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Rendering/KeywordProbe.shader");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Rendering/ShaderProbe.prefab");
        if (shader == null || prefab == null) { throw new InvalidOperationException("The committed ShaderProbe fixture is required."); }
        var settings = AssetDatabase.LoadAssetAtPath<AssetShaderSettings>(Root + "/Settings.asset");
        if (settings == null)
        {
            RequireNewAssetPath(Root + "/Settings.asset");
            settings = ScriptableObject.CreateInstance<AssetShaderSettings>();
            settings.EnableProcessing = true;
            settings.RequiredVariants = new[] { Required(shader), Required(shader, "PROBE_GREEN") };
            settings.Exclusions = new[] { Exclude(shader, "PROBE_EXCLUDED") };
            AssetDatabase.CreateAsset(settings, Root + "/Settings.asset");
        }
        var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(GroupsPath);
        if (groups == null)
        {
            RequireNewAssetPath(GroupsPath);
            var collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(Root + "/Variants.shadervariants");
            if (collection == null)
            {
                RequireNewAssetPath(Root + "/Variants.shadervariants");
                collection = AssetShaderCollection.Generate(settings, Root + "/Variants.shadervariants");
            }
            groups = ScriptableObject.CreateInstance<AssetGroups>();
            groups.Shaders = settings;
            groups.AddressMode = ResourceAddressMode.GroupAndAssetName;
            groups.OutputDirectory = "{projectDir}/BuildArtifacts/ShaderVariantDemo/{version}";
            groups.Groups = new[] { new ResourceGroup { Id = "ShaderDemo", Assets = new[] {
                new ResourceAddress { Asset = prefab }, new ResourceAddress { Asset = collection } } } };
            AssetDatabase.CreateAsset(groups, GroupsPath);
        }
        if (!File.Exists(ScenePath)) { RequireNewAssetPath(ScenePath); CreateScene(); }
        // Save only assets created here; do not flush unrelated dirty user assets.
    }

    private static void CreateScene()
    {
        var previous = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            var host = new GameObject("Shader Variant Demo");
            SceneManager.MoveGameObjectToScene(host, scene);
            host.AddComponent<ShaderVariantDemo>();
            var cameraObject = new GameObject("Demo Camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            cameraObject.transform.SetParent(host.transform);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -10);
            camera.orthographic = true;
            camera.orthographicSize = 2;
            camera.aspect = 2;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            EditorSceneManager.SaveScene(scene, ScenePath);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
            if (previous.IsValid()) { SceneManager.SetActiveScene(previous); }
        }
    }

    [MenuItem("AI Native/Shader Variant Demo/Open scene")]
    public static void OpenScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("Exit Play before opening the demo scene."); }
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { EditorSceneManager.OpenScene(ScenePath); }
    }

    [MenuItem("AI Native/Shader Variant Demo/Build current configuration")]
    public static void BuildCurrent()
    {
        var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(GroupsPath);
        Validate(groups);
        AssetBuilder.Build(groups);
        Debug.Log("Shader demo bundles: " + groups.LastBuildPath + ". Open Demo.unity and enter Play; no StreamingAssets deployment is needed.");
    }

    public static void Validate(AssetGroups groups)
    {
        if (groups == null || groups.Shaders == null) { throw new InvalidOperationException("Create the demo assets and assign Shader Settings."); }
        if (groups.AddressMode != ResourceAddressMode.GroupAndAssetName || !groups.Shaders.EnableProcessing)
        { throw new InvalidOperationException("Demo requires GroupAndAssetName addresses and Enable Processing."); }
        AssetBuildPlan.Create(groups);
        var demo = groups.Groups.SingleOrDefault(x => (string.IsNullOrWhiteSpace(x.Name) ? x.Id : x.Name) == "ShaderDemo");
        if (demo == null) { throw new InvalidOperationException("Expected a ShaderDemo resource group."); }
        var entries = demo.Assets.Select(x => x.Asset).ToArray();
        if (!entries.Any(x => x is GameObject && AssetDatabase.GetAssetPath(x) == "Assets/Rendering/ShaderProbe.prefab") ||
            !entries.Any(x => x is ShaderVariantCollection && Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(x)) == "Variants"))
        { throw new InvalidOperationException("Assign ShaderProbe.prefab and a collection named Variants.shadervariants to ShaderDemo."); }
    }

    // CLI-only: the caller must prepare a disposable project copy before deployment/build.
    public static void BuildPlayer()
    {
        RequireIsolation();
        var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(GroupsPath);
        Validate(groups);
        AssetDeployment.CopyLatestToStreamingAssets(groups);
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        try
        {
            scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<ShaderVariantDemo>()).Single().ContentVersion = groups.LastBuildVersion;
            EditorSceneManager.SaveScene(scene);
        }
        finally { EditorSceneManager.CloseScene(scene, true); }
        var target = UnityEditor.Build.NamedBuildTarget.Standalone;
        var backend = PlayerSettings.GetScriptingBackend(target);
        bool strict = PlayerSettings.strictShaderVariantMatching;
        bool autoApi = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        try
        {
            PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.Mono2x);
            PlayerSettings.strictShaderVariantMatching = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            string output = Argument("-variantPlayerOutput");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var result = AssetPlayerBuildSupport.BuildDeployedPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath }, locationPathName = output, target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development });
            if (result.summary.result != BuildResult.Succeeded) { throw new InvalidOperationException("Shader demo Player build failed."); }
        }
        finally
        {
            PlayerSettings.SetScriptingBackend(target, backend);
            PlayerSettings.strictShaderVariantMatching = strict;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, apis);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, autoApi);
        }
    }

    public static void BuildCurrentForBackend()
    {
        RequireIsolation();
        var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(GroupsPath);
        groups.BuildBackend = Argument("-variantBackend") == "builtin" ? AssetBuildBackend.Builtin : AssetBuildBackend.ScriptableBuildPipeline;
        EditorUtility.SetDirty(groups);
        AssetDatabase.SaveAssetIfDirty(groups);
        BuildCurrent();
        WriteVerificationMetadata(groups);
    }

    public static void PrepareVerificationCase()
    {
        RequireIsolation();
        string state = Argument("-variantCase");
        if (state != "retained" && state != "stripped" && state != "restored") { throw new ArgumentException("Unknown verification case."); }
        var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(GroupsPath);
        var settings = groups.Shaders;
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Rendering/KeywordProbe.shader");
        settings.RequiredVariants = state == "stripped" ? new[] { Required(shader) } : new[] { Required(shader), Required(shader, "PROBE_GREEN") };
        settings.RuntimeCollections = Array.Empty<ShaderVariantCollection>();
        settings.Exclusions = state == "stripped" ? new[] { Exclude(shader, "PROBE_EXCLUDED"), Exclude(shader, "PROBE_GREEN") } : new[] { Exclude(shader, "PROBE_EXCLUDED") };
        settings.EnableProcessing = true;
        string folder = "Verification-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder(Root, folder);
        var collection = AssetShaderCollection.Generate(settings, Root + "/" + folder + "/Variants.shadervariants");
        groups.Groups.Single(x => x.Id == "ShaderDemo").Assets = new[] {
            new ResourceAddress { Asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Rendering/ShaderProbe.prefab") },
            new ResourceAddress { Asset = collection } };
        groups.BuildBackend = Argument("-variantBackend") == "builtin" ? AssetBuildBackend.Builtin : AssetBuildBackend.ScriptableBuildPipeline;
        EditorUtility.SetDirty(settings);
        EditorUtility.SetDirty(groups);
        AssetDatabase.SaveAssets();
        BuildCurrent();
        WriteVerificationMetadata(groups);
    }

    private static void WriteVerificationMetadata(AssetGroups groups)
    {
        File.WriteAllText("variant-last-build.txt", Path.GetFullPath(groups.LastBuildPath));
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Rendering/KeywordProbe.shader");
        var rules = groups.Shaders.Exclusions.Select((rule, index) => new { rule, index }).Where(x =>
            x.rule.Shader == shader && (!x.rule.MatchPassType || x.rule.PassType == PassType.ScriptableRenderPipelineDefaultUnlit) &&
            (string.IsNullOrEmpty(x.rule.PassName) || x.rule.PassName == "Forward") &&
            x.rule.RequiredKeywords.All(k => k == "PROBE_GREEN") && !x.rule.ForbiddenKeywords.Contains("PROBE_GREEN"));
        File.WriteAllText("variant-green-rule-indices.txt", string.Join(",", rules.Select(x => x.index)));
    }

    private static AssetShaderVariant Required(Shader shader, params string[] keywords) => new AssetShaderVariant {
        Shader = shader, PassType = PassType.ScriptableRenderPipelineDefaultUnlit, PassName = "Forward", Keywords = keywords };
    private static AssetShaderExclusion Exclude(Shader shader, string keyword) => new AssetShaderExclusion {
        Shader = shader, MatchPassType = true, PassType = PassType.ScriptableRenderPipelineDefaultUnlit,
        PassName = "Forward", RequiredKeywords = new[] { keyword } };
    private static void RequireNewAssetPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta"))
        { throw new IOException("Refusing to replace an existing asset path: " + path); }
    }
    private static void RequireIsolation()
    {
        if (!File.Exists(".shader-variant-isolated")) { throw new InvalidOperationException("Use scripts/verify_shader_variant_demo.py; Player deployment is restricted to an isolated project."); }
    }
    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException("Required argument: " + name); }
        return args[index + 1];
    }
}
