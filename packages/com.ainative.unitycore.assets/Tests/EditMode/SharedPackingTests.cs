using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class SharedPackingTests
    {
        [Test]
        public void AB_AC_ABC_AreDistinctAndExplicitOwnerWins()
        {
            string root = "Assets/PackingTest-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(root));
            var settings = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                Texture2D Texture(string name)
                {
                    var texture = new Texture2D(1, 1);
                    AssetDatabase.CreateAsset(texture, root + "/" + name + ".asset");
                    return texture;
                }
                var ab = Texture("ab"); var ac = Texture("ac"); var abc = Texture("abc");
                ResourceAddress Entry(string id, Texture2D texture)
                {
                    var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = texture };
                    AssetDatabase.CreateAsset(material, root + "/" + id + ".mat");
                    return new ResourceAddress { Asset = material };
                }
                settings.Groups = new[] {
                    new ResourceGroup { Id = "A", Assets = new[] { Entry("a-ab", ab), Entry("a-ac", ac), Entry("a-abc", abc) } },
                    new ResourceGroup { Id = "B", Assets = new[] { Entry("b-ab", ab), Entry("b-abc", abc) } },
                    new ResourceGroup { Id = "C", Assets = new[] { Entry("c-ac", ac), Entry("c-abc", abc) } }
                };
                AssetDatabase.SaveAssets();
                var plan = AssetBuildPlan.Create(settings);
                Assert.AreEqual(3, new[] { plan.Owners[root + "/ab.asset"], plan.Owners[root + "/ac.asset"], plan.Owners[root + "/abc.asset"] }.Distinct().Count());
                settings.Groups = settings.Groups.Concat(new[] { new ResourceGroup { Id = "Explicit", Assets = new[] { new ResourceAddress { Asset = abc } } } }).ToArray();
                var explicitPlan = AssetBuildPlan.Create(settings);
                StringAssert.StartsWith("group_", explicitPlan.Owners[root + "/abc.asset"]);
                Array.Reverse(settings.Groups);
                CollectionAssert.AreEqual(explicitPlan.Owners, AssetBuildPlan.Create(settings).Owners);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); AssetDatabase.DeleteAsset(root); }
        }
        [Test]
        public void ActualCrossGroupDependencyCycleIsRejected()
        {
            string root = "Assets/CyclePacking-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(root));
            var settings = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                var a = ScriptableObject.CreateInstance<AssetGroups>();
                var b = ScriptableObject.CreateInstance<AssetGroups>();
                AssetDatabase.CreateAsset(a, root + "/a.asset");
                AssetDatabase.CreateAsset(b, root + "/b.asset");
                ResourceGroup Group(string id, UnityEngine.Object asset) => new ResourceGroup { Id = id,
                    Assets = new[] { new ResourceAddress { Asset = asset } } };
                a.Groups = new[] { Group("to-b", b) };
                b.Groups = new[] { Group("to-a", a) };
                EditorUtility.SetDirty(a); EditorUtility.SetDirty(b); AssetDatabase.SaveAssets();
                settings.Groups = new[] { Group("A", a), Group("B", b) };
                var failure = Assert.Throws<AssetException>(() => AssetBuildPlan.Create(settings));
                Assert.AreEqual("CyclicBundles", failure.Code);
                StringAssert.Contains(" -> ", failure.Message);
            }
            finally { AssetDatabase.DeleteAsset(root); UnityEngine.Object.DestroyImmediate(settings); }
        }
        [Test]
        public void IncompleteRealReportCoverageCannotPass()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            var plan = AssetBuildPlan.Create(settings);
            string output = Path.GetFullPath("BuildArtifacts/IncompleteProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            Assert.NotNull(BuildPipeline.BuildAssetBundles(output, plan.Definitions,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64));
            plan.Owners.Add("Assets/ExpectedButMissing.asset", plan.Definitions[0].assetBundleName);
            var failure = Assert.Throws<AssetException>(() => AssetBuilder.ValidateReport(plan, BuildReport.GetLatestReport(), output));
            Assert.AreEqual("OwnershipVerificationFailed", failure.Code);
            StringAssert.Contains("Missing evidence: Assets/ExpectedButMissing.asset", failure.Message);
        }
        [Test]
        public void SameTypeAndNameSubAssetsAreRejected()
        {
            string path = "Assets/Ambiguous-" + Guid.NewGuid().ToString("N") + ".asset";
            var source = new Texture2D(2, 2);
            var settings = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                AssetDatabase.CreateAsset(source, path);
                for (int i = 0; i < 2; i++)
                {
                    var sprite = Sprite.Create(source, new Rect(0, 0, 2, 2), Vector2.zero);
                    sprite.name = "duplicate";
                    AssetDatabase.AddObjectToAsset(sprite, source);
                }
                AssetDatabase.SaveAssets();
                settings.Groups = new[] { new ResourceGroup { Id = "Sprites", Assets = new[] { new ResourceAddress { Asset = source } } } };
                Assert.Throws<AssetException>(() => AssetBuildPlan.Create(settings));
            }
            finally { AssetDatabase.DeleteAsset(path); UnityEngine.Object.DestroyImmediate(settings); }
        }
        [Test]
        public void MissingReportCannotPassOwnershipVerification()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            Assert.Throws<AssetException>(() => AssetBuilder.ValidateReport(AssetBuildPlan.Create(settings), null, "Library"));
        }
        [Test]
        public void RealDuplicatedDependenciesFailArtifactVerification()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            var plan = AssetBuildPlan.Create(settings);
            string output = Path.GetFullPath("BuildArtifacts/DuplicateProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            var definitions = plan.Definitions.Where(x => x.assetNames.Contains("Assets/Probe/A.prefab") || x.assetNames.Contains("Assets/Probe/B.prefab"))
                .Select(x => new AssetBundleBuild { assetBundleName = x.assetBundleName, assetNames = x.assetNames.Where(p => p.EndsWith(".prefab")).ToArray() }).ToArray();
            Assert.NotNull(BuildPipeline.BuildAssetBundles(output, definitions, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64));
            var error = Assert.Throws<AssetException>(() => AssetBuilder.ValidateReport(plan, BuildReport.GetLatestReport(), output));
            Assert.AreEqual("OwnershipVerificationFailed", error.Code);
            StringAssert.Contains("SharedMaterial.mat", File.ReadAllText(Path.Combine(output, "ownership-errors.txt")));
        }
        [Test]
        public void ExistingPublishedDirectoryIsNeverOverwritten()
        {
            string output = Path.GetFullPath("Library/ExistingAssets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "marker"), "original");
            Assert.Throws<IOException>(() => AssetBuilder.Build(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"), output));
            Assert.AreEqual("original", File.ReadAllText(Path.Combine(output, "marker")));
        }
    }
}
