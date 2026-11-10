using System;
using System.IO;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AssetsDemoSetup
{
    public static void ReloadOn() { SetReload(false); }
    public static void ReloadOff() { SetReload(true); }
    private static void SetReload(bool disabled)
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = disabled ? EnterPlayModeOptions.DisableDomainReload : EnterPlayModeOptions.None;
        AssetDatabase.SaveAssets();
    }
    public static void Create()
    {
        const string root = "Assets/Probe";
        var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<AssetGroups>();
            AssetDatabase.CreateAsset(settings, "Assets/ResourceGroups.asset");
        }
        if (AssetDatabase.LoadAssetAtPath<DemoData>(root + "/Data.asset") == null) { AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<DemoData>(), root + "/Data.asset"); }
        if (!File.Exists(root + "/Tone.wav"))
        {
            using var writer = new BinaryWriter(File.Create(root + "/Tone.wav"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 1600);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(1600);
            for (int i = 0; i < 800; i++) { writer.Write((short)(Math.Sin(i * Math.PI * 440 / 4000) * 1000)); }
        }
        AssetDatabase.ImportAsset(root + "/Tone.wav");
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(root + "/SpriteSet.asset") == null)
        {
            var texture = new Texture2D(2, 2) { name = "sprite-texture" };
            texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }); texture.Apply();
            AssetDatabase.CreateAsset(texture, root + "/SpriteSet.asset");
            var sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero); sprite.name = "icon";
            AssetDatabase.AddObjectToAsset(sprite, texture);
        }
        var content = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        SceneManager.MoveGameObjectToScene(cube, content);
        cube.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(root + "/SharedMaterial.mat");
        EditorSceneManager.SaveScene(content, root + "/Content.unity");
        EditorSceneManager.CloseScene(content, true);
        ResourceGroup Group(string id, string path) => new ResourceGroup { Id = id, Assets = new[] { new ResourceAddress { Asset = AssetDatabase.LoadMainAssetAtPath(path) } } };
        settings.AddressMode = ResourceAddressMode.FullPath;
        settings.Groups = new[] { Group("A", root + "/A.prefab"), Group("B", root + "/B.prefab"), Group("Data", root + "/Data.asset"),
            Group("Scene", root + "/Content.unity"), Group("Audio", root + "/Tone.wav"), Group("Sprites", root + "/SpriteSet.asset") };
        EditorUtility.SetDirty(settings);
        var bootstrap = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var host = new GameObject("Assets Demo");
        SceneManager.MoveGameObjectToScene(host, bootstrap);
        host.AddComponent<AssetsDemoBootstrap>();
        var camera = new GameObject("Camera");
        SceneManager.MoveGameObjectToScene(camera, bootstrap);
        camera.AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -6);
        EditorSceneManager.SaveScene(bootstrap, "Assets/Bootstrap.unity");
        EditorSceneManager.CloseScene(bootstrap, true);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Bootstrap.unity", true) };
        PlayerSettings.runInBackground = true;
        AssetDatabase.SaveAssets();
    }
    public static void BuildContent()
    {
        string output = Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
        // Unity may recreate a moved empty folder from its retained .meta; never remove actual content.
        if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length == 0) { AssetDatabase.DeleteAsset("Assets/StreamingAssets/AiNativeAssets"); }
        AssetBuilder.Build(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"), output);
        AssetDatabase.Refresh();
    }
    public static void BuildMono() { BuildPlayer(ScriptingImplementation.Mono2x, "Mono"); }
    public static void BuildIl2Cpp() { BuildPlayer(ScriptingImplementation.IL2CPP, "Il2Cpp"); }
    private static void BuildPlayer(ScriptingImplementation backend, string name)
    {
        string content = Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
        var target = UnityEditor.Build.NamedBuildTarget.Standalone;
        var previous = PlayerSettings.GetScriptingBackend(target);
        try
        {
            PlayerSettings.SetScriptingBackend(target, backend);
            string output = Path.GetFullPath("../../../TestResults/assets-players/" + name + "/AssetsDemo.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Bootstrap.unity" }, locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = backend == ScriptingImplementation.Mono2x ? BuildOptions.Development : BuildOptions.None,
                assetBundleManifestPath = Path.Combine(content, File.ReadAllText(Path.Combine(content, "root-manifest.txt")))
            });
            if (report.summary.result != BuildResult.Succeeded) { throw new Exception("Player build failed: " + report.summary.result); }
        }
        finally { PlayerSettings.SetScriptingBackend(target, previous); }
    }
}
