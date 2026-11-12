using System;
using System.Collections;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.Assets.Editor;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class ShaderVariantDemoTests
{
    [SetUp]
    public void IsolatedProjectOnly()
    {
        if (!File.Exists(".shader-variant-isolated")) { Assert.Ignore("Run this fixture in the verification script's isolated project."); }
    }

    [Test]
    public void CreatePreservesExistingDemoConfiguration()
    {
        var files = Directory.GetFiles(ShaderVariantDemoSetup.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(x => x, File.ReadAllBytes);
        ShaderVariantDemoSetup.Create();
        foreach (var file in files) { CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key), file.Key); }
        ShaderVariantDemoSetup.Validate(AssetDatabase.LoadAssetAtPath<AssetGroups>(ShaderVariantDemoSetup.GroupsPath));
    }

    [Test]
    public void RequiredAndExcludedGreenFailsBeforePublishingCollection()
    {
        var original = AssetDatabase.LoadAssetAtPath<AssetGroups>(ShaderVariantDemoSetup.GroupsPath).Shaders;
        var settings = Object.Instantiate(original);
        string path = ShaderVariantDemoSetup.Root + "/Conflict-" + Guid.NewGuid().ToString("N") + ".shadervariants";
        try
        {
            var green = settings.RequiredVariants.Single(x => x.Keywords.Contains("PROBE_GREEN"));
            settings.Exclusions = new[] { new AssetShaderExclusion { Shader = green.Shader,
                MatchPassType = true, PassType = green.PassType, PassName = green.PassName,
                RequiredKeywords = new[] { "PROBE_GREEN" } } };
            Assert.AreEqual("RequiredShaderExcluded", Assert.Throws<AssetException>(() => AssetShaderCollection.Generate(settings, path)).Code);
            Assert.IsFalse(File.Exists(path));
        }
        finally { Object.DestroyImmediate(settings); }
    }

    [UnityTest]
    public IEnumerator EditorDisplaysRealBundleContentAndReleasesIt()
    {
        EditorSceneManager.OpenScene(ShaderVariantDemoSetup.ScenePath);
        bool reload = !EditorSettings.enterPlayModeOptionsEnabled ||
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
        yield return new EnterPlayMode(expectDomainReload: reload);
        yield return UniTask.ToCoroutine(async () =>
        {
            var demo = Object.FindFirstObjectByType<ShaderVariantDemo>();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            await UniTask.WaitUntil(() => demo.Ready, cancellationToken: timeout.Token);
            Assert.IsTrue(File.Exists(Path.Combine(demo.Source, "catalog.json")));
            Assert.IsTrue(AssetBundle.GetAllLoadedAssetBundles().Any());
            var camera = demo.GetComponentInChildren<Camera>();
            demo.SetGreen(false);
            await UniTask.NextFrame();
            AssertColor(camera, false);
            demo.SetGreen(true);
            await UniTask.NextFrame();
            AssertColor(camera, true);
            await UniTask.DelayFrame(3);
            Assert.IsTrue(demo.Ready, "The interactive display must remain alive.");
            await demo.CloseAsync();
            Assert.IsFalse(demo.Ready);
            Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles());
        });
        yield return new ExitPlayMode();
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        if (!File.Exists(".shader-variant-isolated")) { yield break; }
        if (EditorApplication.isPlaying) { yield return new ExitPlayMode(); }
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
    }

    private static void AssertColor(Camera camera, bool green)
    {
        var target = RenderTexture.GetTemporary(256, 128, 24);
        var previous = RenderTexture.active;
        var pixels = new Texture2D(256, 128, TextureFormat.RGB24, false);
        try
        {
            RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 256, 128), 0, 0);
            pixels.Apply();
            Directory.CreateDirectory("TestResults/VariantEditor");
            File.WriteAllBytes("TestResults/VariantEditor/" + (green ? "green" : "red") + ".png", pixels.EncodeToPNG());
            Color value = pixels.GetPixel(32, 64);
            Assert.Greater(green ? value.g : value.r, .7f);
            Assert.Less(green ? value.r : value.g, .2f);
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.Destroy(pixels); }
    }
}
