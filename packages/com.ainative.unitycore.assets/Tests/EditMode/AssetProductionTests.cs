using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Assets.Editor.Tests
{
    public sealed class AssetProductionTests
    {
        [Test]
        public void EditorFactoryPreservesLegacyDefaultLiteralAndAcceptsFixedOptions()
        {
            var groups = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                Assert.IsNotNull(EditorAssets.Create(groups, default));
                Assert.IsNotNull(EditorAssets.Create(groups));
                Assert.IsNotNull(EditorAssets.CreateWithOptions(groups, new AssetRuntimeOptions(1, 1)));
                Assert.IsNotNull(EditorAssets.CreateConfigured(groups, options: new AssetRuntimeOptions(2, 1)));
            }
            finally { UnityEngine.Object.DestroyImmediate(groups); }
        }

        [Test]
        public void AnalysisWarningsMeasureDeduplicatedDependencyFilesAndKeepLegacyLoadingSeparate()
        {
            var groups = ScriptableObject.CreateInstance<AssetGroups>();
            string directory = Path.Combine(Path.GetTempPath(), "asset-analysis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                groups.Groups = new[] { "A", "B" }.Select(name => new ResourceGroup { Id = name,
                    Assets = new[] { new ResourceAddress { Asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Probe/" + name + ".prefab") } } }).ToArray();
                var plan = AssetBuildPlan.Create(groups);
                var catalog = new AssetCatalog { UnityVersion = Application.unityVersion, Assets = plan.Addresses,
                    Bundles = plan.Definitions.Select(x => new BundleEntry { Name = x.assetBundleName, Size = 10,
                        Dependencies = plan.Dependencies[x.assetBundleName].ToArray() }).ToArray() };
                groups.BundleWarningBytes = 9;
                groups.TotalWarningBytes = 1;
                LogAssert.Expect(LogType.Warning, "Total bundle file bytes exceed threshold: " + catalog.Bundles.Length * 10);
                foreach (var bundle in catalog.Bundles) { LogAssert.Expect(LogType.Warning, bundle.Name + " exceeds file byte threshold: 10"); }
                AssetBuildAnalysis.Write(groups, plan, catalog, 7, directory);
                var report = AssetBuildAnalysis.Read(directory);
                Assert.AreEqual(catalog.Bundles.Length + 1, report.Warnings.Length);
                Assert.AreEqual(7, report.ContentVersion);
                foreach (var source in report.Sources.Where(x => !string.IsNullOrEmpty(x.Address)))
                { Assert.AreEqual(20, source.DependencyFileBytes); }
                File.Delete(Path.Combine(directory, "build-analysis.json"));
                Assert.AreEqual("AnalysisMissing", Assert.Throws<AssetException>(() => AssetBuildAnalysis.Read(directory)).Code);
            }
            finally
            {
                File.Delete(Path.Combine(directory, "build-analysis.json"));
                Directory.Delete(directory);
                UnityEngine.Object.DestroyImmediate(groups);
            }
        }

        [Test]
        public void DiagnosticExportAndDifferencePreserveSnapshotAndGeneration()
        {
            var tracker = new AssetOwnershipTracker();
            var scheduler = new AssetLoadScheduler(new AssetRuntimeOptions());
            var dependencies = new List<string> { "shared" };
            long id = tracker.Add("lease", "asset", 1, dependencies: dependencies);
            var before = tracker.Snapshot(scheduler);
            dependencies.Clear();
            tracker.Change(id, 2, "pooled");
            var after = tracker.Snapshot(scheduler);
            Assert.AreEqual(1, before.Owners[0].OwnerId);
            CollectionAssert.AreEqual(new[] { "shared" }, before.Owners[0].Dependencies);
            StringAssert.Contains("Changed: " + id, AssetDiagnosticsExport.Difference(before, after));
            StringAssert.Contains("Different runtime generations", AssetDiagnosticsExport.Difference(before, new AssetOwnershipTracker().Snapshot(scheduler)));
            string path = Path.GetTempFileName();
            try
            {
                AssetDiagnosticsExport.Write(new AssetDiagnostics(new List<string>(), new List<string>(), new List<string>(), 0, ownership: after), path);
                string json = File.ReadAllText(path);
                StringAssert.Contains(after.Generation, json);
                StringAssert.Contains("pooled", json);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void ShaderSessionRestoresContextAfterFailureAndReportsUnobservedVariants()
        {
            var groups = ScriptableObject.CreateInstance<AssetGroups>();
            var settings = ScriptableObject.CreateInstance<AssetShaderSettings>();
            string directory = Path.Combine(Path.GetTempPath(), "asset-shader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                groups.Shaders = settings;
                settings.RequiredVariants = new[] { new AssetShaderVariant { Shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture") } };
                var plan = AssetBuildPlan.Create(groups);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using var session = new AssetShaderBuildSession(groups, plan);
                    Assert.AreSame(session, AssetShaderBuildSession.Active);
                    throw new InvalidOperationException("simulated build failure");
                });
                Assert.IsNull(AssetShaderBuildSession.Active);
                using (var next = new AssetShaderBuildSession(groups, plan)) { next.Write(directory); }
                Assert.IsNull(AssetShaderBuildSession.Active);
                StringAssert.Contains("not observed; retention unproven", File.ReadAllText(Path.Combine(directory, "shader-build-report.json")));
            }
            finally
            {
                File.Delete(Path.Combine(directory, "shader-build-report.json"));
                Directory.Delete(directory);
                UnityEngine.Object.DestroyImmediate(groups);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void PlatformFiltersAffectAutoCollectionButNotExplicitEntriesOrDependencies()
        {
            var settings = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                var folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Probe");
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Probe/A.prefab");
                settings.Groups = new[] { new ResourceGroup
                {
                    Id = "filtered", Folders = new[] { folder }, Assets = new[] { new ResourceAddress { Asset = prefab } },
                    Filter = new ResourceFilter { ExcludedFolders = new[] { folder } },
                    PlatformFilters = new[] { new PlatformResourceFilter
                    {
                        Target = BuildTarget.Android, OverrideFolders = true, OverrideTypes = true,
                        Filter = new ResourceFilter { IncludedTypes = new[] { new ResourceTypeFilter { AssemblyQualifiedName = typeof(Texture).AssemblyQualifiedName } } }
                    } }
                } };
                var windows = AssetBuildPlan.Create(settings);
                Assert.AreEqual(1, windows.Addresses.Length);
                Assert.True(windows.Owners.ContainsKey("Assets/Probe/SharedTexture.asset"));
                Assert.IsTrue(windows.CollectionDecisions.Any(x => x.Contains("excluded-directory")));
                var android = AssetBuildPlan.Create(settings, BuildTarget.Android);
                Assert.Greater(android.Addresses.Length, windows.Addresses.Length);
                Assert.IsTrue(android.CollectionDecisions.Any(x => x.Contains("excluded-type")));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void BuildComparisonUsesGuidForMovesAndDigestForBundleChanges()
        {
            AssetBuildAnalysis Report(string path, string digest, long size) => new AssetBuildAnalysis
            {
                Sources = new[] { new AssetSourceAnalysis { Guid = "same", Path = path, Address = path, Bundle = "group.bundle" } },
                Bundles = new[] { new BundleEntry { Name = "group.bundle", Sha256 = digest, Size = size } }, TotalFileBytes = size
            };
            var changes = AssetBuildAnalysis.Compare(Report("old", "aaa", 10), Report("new", "bbb", 20));
            Assert.True(changes.Any(x => x.StartsWith("changed\tsame")));
            Assert.True(changes.Any(x => x.StartsWith("bundle-changed")));
            Assert.False(changes.Any(x => x.StartsWith("added")));
            Assert.AreEqual(3, changes.Length);
        }

        [Test]
        public void ShaderRulesMatchPassAndKeywordsWithoutAssumingWhitelist()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            var rule = new AssetShaderExclusion { Shader = shader, MatchPassType = true, PassType = PassType.ScriptableRenderPipeline,
                PassName = "Forward", RequiredKeywords = new[] { "DROP" }, ForbiddenKeywords = new[] { "KEEP" } };
            Assert.True(AssetShaderBuildSession.Matches(rule, shader, PassType.ScriptableRenderPipeline, "Forward", new[] { "DROP" }));
            Assert.False(AssetShaderBuildSession.Matches(rule, shader, PassType.ScriptableRenderPipeline, "Depth", new[] { "DROP" }));
            Assert.False(AssetShaderBuildSession.Matches(rule, shader, PassType.ScriptableRenderPipeline, "Forward", new[] { "DROP", "KEEP" }));
            Assert.False(AssetShaderBuildSession.Matches(rule, shader, PassType.ScriptableRenderPipeline, "Forward", new[] { "UNKNOWN" }));
        }

        [Test]
        public void RequiredShaderVariantsCannotBeExcluded()
        {
            var settings = ScriptableObject.CreateInstance<AssetShaderSettings>();
            try
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
                settings.RequiredVariants = new[] { new AssetShaderVariant { Shader = shader, Keywords = new[] { "REQUIRED" } } };
                settings.Exclusions = new[] { new AssetShaderExclusion { Shader = shader, PassName = "Forward", RequiredKeywords = new[] { "REQUIRED" } } };
                var error = Assert.Throws<AssetException>(() => AssetShaderBuildSession.Validate(settings));
                Assert.AreEqual("RequiredShaderExcluded", error.Code);
                settings.Exclusions = Array.Empty<AssetShaderExclusion>();
                Assert.DoesNotThrow(() => AssetShaderBuildSession.Validate(settings));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }
    }
}
