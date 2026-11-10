using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class SbpBuildTests
    {
        private static AssetGroups Settings() => UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"));
        private static string Output() => Path.GetFullPath("BuildArtifacts/SbpTests/" + Guid.NewGuid().ToString("N"));

        [TestCase(BundleNamingMode.StableHash, ResourceAddressMode.FullPath)]
        [TestCase(BundleNamingMode.GroupName, ResourceAddressMode.FullPath)]
        [TestCase(BundleNamingMode.StableHash, ResourceAddressMode.GroupAndAssetName)]
        [TestCase(BundleNamingMode.GroupName, ResourceAddressMode.GroupAndAssetName)]
        public void BackendsProduceEquivalentCatalogSemantics(BundleNamingMode naming, ResourceAddressMode addresses)
        {
            var settings = Settings();
            try
            {
                settings.BundleNaming = naming; settings.AddressMode = addresses;
                settings.BuildBackend = AssetBuildBackend.Builtin;
                var builtin = AssetBuilder.Build(settings, Output());
                settings.BuildBackend = AssetBuildBackend.ScriptableBuildPipeline;
                string output = Output();
                var sbp = AssetBuilder.Build(settings, output);
                CollectionAssert.AreEqual(builtin.Assets.Select(x => x.Address + "|" + x.Path + "|" + x.Bundle), sbp.Assets.Select(x => x.Address + "|" + x.Path + "|" + x.Bundle));
                CollectionAssert.AreEqual(builtin.Bundles.Select(x => x.Name + "|" + string.Join(",", x.Dependencies)), sbp.Bundles.Select(x => x.Name + "|" + string.Join(",", x.Dependencies)));
                StringAssert.EndsWith("link.xml", AssetPlayerBuildSupport.ValidatePreservation(output, out var metadata));
                Assert.AreEqual("2.6.2", metadata.SbpVersion);
                Assert.IsNull(AssetPlayerBuildSupport.ActiveLinkXml);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void CachedBuildTracksChangedMovedAndDeletedFolderAssets()
        {
            string root = "Assets/SbpCache-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(root));
            var settings = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                var texture = new Texture2D(1, 1);
                AssetDatabase.CreateAsset(texture, root + "/shared.asset");
                ResourceGroup Group(string id)
                {
                    AssetDatabase.CreateFolder(root, id);
                    var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = texture };
                    AssetDatabase.CreateAsset(material, root + "/" + id + "/item.mat");
                    return new ResourceGroup { Id = id, Folders = new[] { AssetDatabase.LoadMainAssetAtPath(root + "/" + id) } };
                }
                settings.Groups = new[] { Group("A"), Group("B") };
                settings.BundleNaming = BundleNamingMode.GroupName;
                AssetDatabase.SaveAssets();
                string coldOutput = Output();
                var cold = AssetBuilder.Build(settings, coldOutput);
                string hotOutput = Output();
                var hot = AssetBuilder.Build(settings, hotOutput);
                CollectionAssert.AreEqual(cold.Bundles.Select(x => x.Sha256), hot.Bundles.Select(x => x.Sha256));
                StringAssert.Contains("Processed: 0, Cached:", File.ReadAllText(Path.Combine(hotOutput, "buildlogtep.json")));
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(root + "/shared.asset");
                texture.SetPixel(0, 0, Color.magenta); texture.Apply(); EditorUtility.SetDirty(texture); AssetDatabase.SaveAssets();
                var changed = AssetBuilder.Build(settings, Output());
                Assert.AreNotEqual(cold.Bundles.Single(x => x.Name == "share_a_b.bundle").Sha256, changed.Bundles.Single(x => x.Name == "share_a_b.bundle").Sha256);
                Assert.AreEqual("", AssetDatabase.MoveAsset(root + "/A/item.mat", root + "/A/moved.mat"));
                AssetDatabase.DeleteAsset(root + "/B/item.mat");
                var moved = AssetBuilder.Build(settings, Output());
                CollectionAssert.AreEquivalent(new[] { root + "/A/moved" }, moved.Assets.Select(x => x.Address));
                Assert.IsFalse(moved.Bundles.Any(x => x.Name == "share_a_b.bundle" || x.Name == "group_b.bundle"));
                var clean = AssetBuilder.Build(settings, Output(), new AssetBuildOptions { UseCache = false });
                CollectionAssert.AreEqual(moved.Bundles.Select(x => x.Sha256), clean.Bundles.Select(x => x.Sha256));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); AssetDatabase.DeleteAsset(root); }
        }

        [Test]
        public void MissingEvidenceMappingAndPreservationFailClosed()
        {
            var settings = Settings();
            try
            {
                var plan = AssetBuildPlan.Create(settings);
                string output = Output();
                bool slim = ScriptableBuildPipeline.slimWriteResults;
                AssetBuilder.Build(settings, output);
                Assert.AreEqual(slim, ScriptableBuildPipeline.slimWriteResults);
                SbpBuildEvidence Read() => JsonUtility.FromJson<SbpBuildEvidence>(File.ReadAllText(Path.Combine(output, "sbp-write-results.json")));
                Assert.Throws<AssetException>(() => SbpBuildEvidence.Validate(plan, null, output));
                var evidence = Read(); evidence.Files[0].Bundle = "unmapped";
                Assert.AreEqual("ReportMappingIncomplete", Assert.Throws<AssetException>(() => SbpBuildEvidence.Validate(plan, evidence, output)).Code);
                evidence = Read();
                foreach (var file in evidence.Files) { file.Objects.RemoveAll(x => x.SourcePath == "Assets/Probe/SharedTexture.asset"); }
                Assert.AreEqual("OwnershipVerificationFailed", Assert.Throws<AssetException>(() => SbpBuildEvidence.Validate(plan, evidence, output)).Code);
                string link = Path.Combine(output, "link.xml");
                File.Move(link, link + ".backup");
                Assert.AreEqual("MissingPreservationFile", Assert.Throws<AssetException>(() => AssetPlayerBuildSupport.ValidatePreservation(output, out _)).Code);
                Assert.IsNull(AssetPlayerBuildSupport.ActiveLinkXml);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void PublicationIsAtomicEvenWhenUnityRetainsSourceHandles()
        {
            string staging = Output(); Directory.CreateDirectory(staging);
            string source = Path.Combine(staging, "bundle"); File.WriteAllText(source, "verified");
            string destination = Output();
            using (var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                AssetBuilder.Publish(staging, destination);
                Assert.AreEqual("verified", File.ReadAllText(Path.Combine(destination, "bundle")));
                Assert.Throws<IOException>(() => AssetBuilder.Publish(staging, destination));
                Assert.AreEqual("verified", File.ReadAllText(Path.Combine(destination, "bundle")));
            }
        }

        [Test]
        public void SbpExceptionRestoresReportPreferences()
        {
            var settings = Settings();
            bool slim = ScriptableBuildPipeline.slimWriteResults;
            try
            {
                var plan = AssetBuildPlan.Create(settings);
                plan.Definitions[0].assetNames = new[] { "Assets/DoesNotExist.prefab" };
                Assert.Throws<ArgumentException>(() => SbpAssetBuild.Build(plan, Output(), new AssetBuildOptions(), new AssetBuildMetadata(), out _));
                Assert.AreEqual(slim, ScriptableBuildPipeline.slimWriteResults);
                Assert.IsNull(AssetPlayerBuildSupport.ActiveLinkXml);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void DependencyClosureReductionRejectsMissingEdgesAndCycles()
        {
            var plan = new AssetBuildPlan { Dependencies = new Dictionary<string, HashSet<string>> {
                ["a"] = new HashSet<string> { "b" }, ["b"] = new HashSet<string> { "c" }, ["c"] = new HashSet<string>() } };
            var actual = new Dictionary<string, HashSet<string>> { ["a"] = new HashSet<string> { "b", "c" }, ["b"] = new HashSet<string> { "c" }, ["c"] = new HashSet<string>() };
            CollectionAssert.AreEquivalent(new[] { "b" }, BundleDependencyGraph.VerifyAndReduce(plan, actual)["a"]);
            actual["b"].Clear();
            Assert.Throws<AssetException>(() => BundleDependencyGraph.VerifyAndReduce(plan, actual));
            actual["b"].Add("a");
            Assert.AreEqual("CyclicBundles", Assert.Throws<AssetException>(() => BundleDependencyGraph.VerifyAndReduce(plan, actual)).Code);
        }

        [Test]
        public void ActualDuplicatedSbpSourcesCannotPassValidation()
        {
            var settings = Settings();
            bool slim = ScriptableBuildPipeline.slimWriteResults;
            try
            {
                settings.Groups = settings.Groups.Where(x => x.Id == "A" || x.Id == "B").ToArray();
                var plan = AssetBuildPlan.Create(settings);
                var definitions = plan.Definitions.Where(x => x.assetNames.Any(p => p.EndsWith(".prefab"))).ToArray();
                string output = Output(); Directory.CreateDirectory(output);
                var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
                var capture = new SbpEvidenceCapture(); tasks.Add(capture);
                ScriptableBuildPipeline.slimWriteResults = false;
                var parameters = new BundleBuildParameters(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, output)
                    { UseCache = false, CacheServerHost = "", NonRecursiveDependencies = false, BundleCompression = BuildCompression.LZ4 };
                Assert.GreaterOrEqual(ContentPipeline.BuildAssetBundles(parameters, new BundleBuildContent(definitions), out _, tasks), ReturnCode.Success);
                var owners = capture.Evidence.Files.Where(f => f.Objects.Any(x => x.SourcePath == "Assets/Probe/SharedMaterial.mat")).Select(f => f.Bundle).Distinct();
                Assert.AreEqual(2, owners.Count());
                File.WriteAllText(Path.Combine(output, "duplicate-evidence.json"), JsonUtility.ToJson(capture.Evidence, true));
                Assert.Throws<AssetException>(() => SbpBuildEvidence.Validate(plan, capture.Evidence, output));
            }
            finally { ScriptableBuildPipeline.slimWriteResults = slim; UnityEngine.Object.DestroyImmediate(settings); }
        }
    }
}
