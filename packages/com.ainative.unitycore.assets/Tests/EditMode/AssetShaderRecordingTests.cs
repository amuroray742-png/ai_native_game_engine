using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace AiNative.UnityCore.Assets.Editor.Tests
{
    public sealed class AssetShaderRecordingTests
    {
        private const string RootKey = "AiNative.Assets.ShaderRecordingTests.Root";
        private const string ScenesKey = RootKey + ".Scenes";
        [Serializable] private sealed class SavedScenes { public SavedScene[] Items; }
        [Serializable] private sealed class SavedScene { public string Path; public bool Loaded; public bool Active; }

        [UnityTest]
        public IEnumerator ActualUrpFramesRecordRuntimeKeywordsAndSurvivePlayTransition()
        {
            if (AssetShaderRecording.IsRecording) { Assert.Ignore("A user shader recording is active."); }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Rendering/ShaderProbe.prefab");
            if (prefab == null || GraphicsSettings.defaultRenderPipeline == null) { Assert.Ignore("Requires the AssetsDemo URP fixture."); }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!Application.isBatchMode && (scene.isDirty || (string.IsNullOrEmpty(scene.path) && scene.rootCount != 0)))
                {
                    Assert.Ignore("Save the open scenes before running the recording fixture.");
                }
            }
            var sceneSetup = new SavedScenes { Items = EditorSceneManager.GetSceneManagerSetup().Select(x =>
                new SavedScene { Path = x.path, Loaded = x.isLoaded, Active = x.isActive }).ToArray() };
            SessionState.SetString(ScenesKey, JsonUtility.ToJson(sceneSetup));
            string folder = "ShaderRecordingTests-" + Guid.NewGuid().ToString("N");
            string root = "Assets/" + folder;
            AssetDatabase.CreateFolder("Assets", folder);
            SessionState.SetString(RootKey, root);
            var settings = ScriptableObject.CreateInstance<AssetShaderSettings>();
            AssetDatabase.CreateAsset(settings, root + "/Settings.asset");
            var groups = ScriptableObject.CreateInstance<AssetGroups>();
            groups.Shaders = settings;
            groups.Groups = new[] { new ResourceGroup { Id = "Probe", Assets = new[] { new ResourceAddress { Asset = prefab } } } };
            AssetDatabase.CreateAsset(groups, root + "/Groups.asset");
            AssetDatabase.SaveAssets();
            AssetShaderRecording.Begin(settings);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(expectDomainReload: reload);
            Assert.IsTrue(AssetShaderRecording.IsRecording);
            foreach (string renderer in new[] { "Forward", "ForwardPlus" })
            {
                var originalDefault = GraphicsSettings.defaultRenderPipeline;
                var originalQuality = QualitySettings.renderPipeline;
                var instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Rendering/ShaderProbe.prefab"));
                var cameraObject = new GameObject("Recording test camera");
                var camera = cameraObject.AddComponent<Camera>();
                var texture = new RenderTexture(256, 128, 24);
                var pixels = new Texture2D(256, 128, TextureFormat.RGB24, false);
                var keywordRenderer = instance.GetComponentsInChildren<Renderer>().Single(x => x.name == "Keyword");
                var material = new Material(keywordRenderer.sharedMaterial);
                keywordRenderer.sharedMaterial = material;
                bool previousAsync = ShaderUtil.allowAsyncCompilation;
                try
                {
                    ShaderUtil.allowAsyncCompilation = false;
                    var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Rendering/" + renderer + ".asset");
                    Assert.IsNotNull(pipeline);
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                    QualitySettings.renderPipeline = pipeline;
                    camera.transform.position = new Vector3(0, 0, -10);
                    camera.orthographic = true;
                    camera.orthographicSize = 2;
                    camera.aspect = 2;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.black;
                    texture.Create();
                    camera.targetTexture = texture;
                    yield return null;
                    Capture(camera, texture, pixels);
                    Color red = pixels.GetPixel(32, 64);
                    Assert.Greater(red.r, .7f, renderer + " red baseline");
                    Assert.Less(red.g, .2f);
                    material.EnableKeyword("PROBE_GREEN");
                    yield return null;
                    Capture(camera, texture, pixels);
                    Color green = pixels.GetPixel(32, 64);
                    Assert.Greater(green.g, .7f, renderer + " runtime green");
                    Assert.Less(green.r, .2f);
                    foreach (int x in new[] { 80, 128, 176, 224 })
                    {
                        Color value = pixels.GetPixel(x, 64);
                        Assert.Greater(value.maxColorComponent, .05f, renderer + " material pixel " + x);
                        Assert.IsFalse(value.r > .8f && value.b > .8f && value.g < .2f, "Pink material");
                    }
                    Directory.CreateDirectory("TestResults/ShaderAutoVerification");
                    File.WriteAllBytes("TestResults/ShaderAutoVerification/" + renderer + ".png", pixels.EncodeToPNG());
                }
                finally
                {
                    ShaderUtil.allowAsyncCompilation = previousAsync;
                    GraphicsSettings.defaultRenderPipeline = originalDefault;
                    QualitySettings.renderPipeline = originalQuality;
                    Object.Destroy(cameraObject);
                    Object.Destroy(instance);
                    Object.Destroy(material);
                    Object.Destroy(pixels);
                    texture.Release();
                    Object.Destroy(texture);
                }
                yield return null;
            }
            yield return new ExitPlayMode();
            Assert.IsTrue(AssetShaderRecording.IsRecording);
            root = SessionState.GetString(RootKey, "");
            var recorded = AssetShaderRecording.Save(root + "/Recorded.shadervariants");
            Assert.IsFalse(AssetShaderRecording.IsRecording);
            var shader = Shader.Find("AI Native/Keyword Probe");
            var greenVariant = new ShaderVariantCollection.ShaderVariant(shader,
                PassType.ScriptableRenderPipelineDefaultUnlit, "PROBE_GREEN");
            Assert.IsTrue(recorded.Contains(greenVariant), "Actual rendered runtime keyword must be recorded.");
            groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(root + "/Groups.asset");
            Assert.AreSame(recorded, groups.Shaders.RuntimeCollections.Single());
            Assert.IsEmpty(groups.Shaders.RequiredVariants);
            var merged = AssetShaderAutoCollection.Generate(groups, root + "/Merged.shadervariants");
            Assert.IsTrue(merged.Contains(greenVariant), "Runtime-only variant must survive the merge.");
            foreach (string name in new[] { "Lit", "Unlit", "Graph", "GraphEmission" })
            {
                var shaderAsset = AssetDatabase.LoadAssetAtPath<Material>("Assets/Rendering/" + name + ".mat").shader;
                Assert.IsTrue(AssetShaderAutoCollection.Read(merged).Any(x => x.shader == shaderAsset), "Missing " + name);
            }
            groups.Groups[0].Assets = groups.Groups[0].Assets.Concat(new[] { new ResourceAddress { Asset = merged } }).ToArray();
            EditorUtility.SetDirty(groups);
            AssetDatabase.SaveAssets();
            foreach (AssetBuildBackend backend in Enum.GetValues(typeof(AssetBuildBackend)))
            {
                BuildAndWarmup(root, backend);
            }
            foreach (string name in new[] { "Recorded.shadervariants", "Merged.shadervariants", "Merged.shadervariants.generation.json" })
            {
                File.Copy(root + "/" + name, "TestResults/ShaderAutoVerification/" + name, true);
            }
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            string root = SessionState.GetString(RootKey, "");
            if (string.IsNullOrEmpty(root)) { yield break; }
            if (EditorApplication.isPlaying) { yield return new ExitPlayMode(); }
            AssetShaderRecording.Cancel();
            AssetDatabase.DeleteAsset(root);
            SessionState.EraseString(RootKey);
            var setup = JsonUtility.FromJson<SavedScenes>(SessionState.GetString(ScenesKey, ""));
            SessionState.EraseString(ScenesKey);
            if (setup.Items.Length == 0 || setup.Items.Any(x => string.IsNullOrEmpty(x.Path)))
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            else
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup.Items.Select(x => new SceneSetup
                    { path = x.Path, isLoaded = x.Loaded, isActive = x.Active }).ToArray());
            }
        }

        private static void BuildAndWarmup(string root, AssetBuildBackend backend)
        {
            var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(root + "/Groups.asset");
            groups.BuildBackend = backend;
            string output = "TestResults/ShaderAutoVerification/" + backend + "-" + Guid.NewGuid().ToString("N");
            var catalog = AssetBuilder.Build(groups, output);
            var bundles = new System.Collections.Generic.List<AssetBundle>();
            try
            {
                foreach (var bundle in catalog.Bundles)
                {
                    var loaded = AssetBundle.LoadFromFile(Path.Combine(output, bundle.Name));
                    Assert.IsNotNull(loaded);
                    bundles.Add(loaded);
                }
                var entry = catalog.Assets.Single(x => x.Path == root + "/Merged.shadervariants");
                var owner = bundles.Single(x => x.name == entry.Bundle);
                var collection = owner.LoadAsset<ShaderVariantCollection>(entry.Path);
                Assert.IsNotNull(collection);
                Assert.Greater(collection.variantCount, 0);
                collection.WarmUp();
                Assert.IsTrue(collection.isWarmedUp);
                File.WriteAllText(output + "/warmup.json", "{\"backend\":\"" + backend + "\",\"variants\":" + collection.variantCount + ",\"warmedUp\":true}");
            }
            finally
            {
                foreach (var bundle in bundles) { bundle.Unload(true); }
            }
        }

        private static void Capture(Camera camera, RenderTexture texture, Texture2D pixels)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = texture });
                RenderTexture.active = texture;
                pixels.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                pixels.Apply();
            }
            finally { RenderTexture.active = previous; }
        }
    }
}
