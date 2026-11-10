using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class DeploymentTests
    {
        private string _root;
        private string _previous;
        private AssetGroups _settings;
        private string _source;

        [SetUp]
        public void SetUp()
        {
            _root = Path.GetFullPath("BuildArtifacts/DeploymentTests/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _previous = Path.Combine(_root, "previous-runtime");
            if (Directory.Exists(DeploymentFiles.Destination)) { Directory.Move(DeploymentFiles.Destination, _previous); }
            _settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"));
            _settings.Version = 7;
            _source = Path.Combine(_root, "source");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(DeploymentFiles.Destination)) { Directory.Delete(DeploymentFiles.Destination, true); }
            if (Directory.Exists(_previous)) { Directory.Move(_previous, DeploymentFiles.Destination); }
            UnityEngine.Object.DestroyImmediate(_settings);
            AssetDatabase.Refresh();
        }

        [TestCase(AssetBuildBackend.Builtin)]
        [TestCase(AssetBuildBackend.ScriptableBuildPipeline)]
        public void DeploysOnlyRuntimeFilesAndPreservesIndependentPlayerEvidence(AssetBuildBackend backend)
        {
            _settings.BuildBackend = backend;
            AssetBuilder.Build(_settings, _source);
            string digest = _settings.LastBuildCatalogSha256;
            Assert.AreEqual(7, _settings.LastBuildVersion);
            Assert.IsFalse(Path.IsPathRooted(_settings.LastBuildPath));
            File.WriteAllText(Path.Combine(_source, "report-extra.txt"), "do not deploy");
            Directory.CreateDirectory(DeploymentFiles.Destination);
            File.WriteAllText(Path.Combine(DeploymentFiles.Destination, "obsolete.bundle"), "old");
            File.WriteAllText(Path.Combine(DeploymentFiles.Destination, "obsolete.bundle.meta"), "old meta");
            string neighbor = Path.Combine(Application.streamingAssetsPath, "deployment-test-neighbor.txt");
            File.WriteAllText(neighbor, "keep");
            string folderMeta = DeploymentFiles.Destination + ".meta";
            byte[] previousMeta = File.Exists(folderMeta) ? File.ReadAllBytes(folderMeta) : null;
            try
            {
                var result = AssetDeployment.CopyLatestToStreamingAssets(_settings);
                Assert.AreEqual(7, result.Version);
                Assert.AreEqual(8, _settings.Version);
                Assert.AreEqual("keep", File.ReadAllText(neighbor));
                var catalog = DeploymentFiles.ValidateRuntime(DeploymentFiles.Destination, true);
                CollectionAssert.AreEquivalent(DeploymentFiles.RuntimeNames(catalog), Directory.GetFiles(DeploymentFiles.Destination)
                    .Where(p => !p.EndsWith(".meta")).Select(Path.GetFileName));
                Assert.IsFalse(File.Exists(Path.Combine(DeploymentFiles.Destination, "obsolete.bundle.meta")));
                if (previousMeta != null) { CollectionAssert.AreEqual(previousMeta, File.ReadAllBytes(folderMeta)); }
                Assert.AreEqual(catalog.Bundles.Length + 1, result.FileCount);
                Assert.AreEqual(catalog.Bundles.Sum(b => b.Size) + new FileInfo(Path.Combine(_source, "catalog.json")).Length, result.TotalBytes);
                Directory.Delete(_source, true);
                string snapshot = DeploymentSnapshot.ValidateDeployed();
                var metadata = DeploymentSnapshot.Validate(snapshot, digest);
                if (backend == AssetBuildBackend.Builtin)
                {
                    foreach (var bundle in catalog.Bundles) { Assert.IsTrue(File.Exists(Path.Combine(snapshot, bundle.Name + ".manifest"))); }
                    Assert.Throws<UnityEditor.Build.BuildFailedException>(() => AssetPlayerBuildSupport.ValidatePlayerDeployment(new BuildPlayerOptions { target = BuildTarget.StandaloneWindows64 }));
                    AssetPlayerBuildSupport.ValidatePlayerDeployment(new BuildPlayerOptions { target = BuildTarget.StandaloneWindows64, assetBundleManifestPath = Path.Combine(snapshot, metadata.PreservationFile) });
                }
                else
                {
                    AssetPlayerBuildSupport.ValidatePlayerDeployment(new BuildPlayerOptions { target = BuildTarget.StandaloneWindows64 });
                    Assert.AreEqual(Path.Combine(snapshot, "link.xml"), AssetPlayerBuildSupport.LinkerFileForCurrentBuild());
                }
            }
            finally { File.Delete(neighbor); if (File.Exists(neighbor + ".meta")) { File.Delete(neighbor + ".meta"); } }
        }

        [Test]
        public void NoLatestRecordRequiresBuildAndFailedBuildKeepsPreviousRecord()
        {
            var empty = ScriptableObject.CreateInstance<AssetGroups>();
            try { Assert.AreEqual("MissingLatestBuild", Assert.Throws<AssetException>(() => AssetDeployment.CopyLatestToStreamingAssets(empty)).Code); }
            finally { UnityEngine.Object.DestroyImmediate(empty); }
            AssetBuilder.Build(_settings, _source);
            string previous = _settings.LastBuildPath;
            string digest = _settings.LastBuildCatalogSha256;
            Assert.Throws<IOException>(() => AssetBuilder.Build(_settings, _source));
            Assert.AreEqual(previous, _settings.LastBuildPath);
            Assert.AreEqual(digest, _settings.LastBuildCatalogSha256);
            File.AppendAllText(Path.Combine(_source, "catalog.json"), " ");
            Assert.AreEqual("LatestBuildChanged", Assert.Throws<AssetException>(() => AssetDeployment.CopyLatestToStreamingAssets(_settings)).Code);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrCorruptSourceLeavesOldContentIntact(bool missing)
        {
            AssetBuilder.Build(_settings, _source);
            AssetDeployment.CopyToStreamingAssets(_source);
            byte[] before = File.ReadAllBytes(Path.Combine(DeploymentFiles.Destination, "catalog.json"));
            var catalog = DeploymentFiles.ValidateRuntime(_source, false);
            string bundle = Path.Combine(_source, catalog.Bundles[0].Name);
            if (missing) { File.Delete(bundle); }
            else { byte[] bytes = File.ReadAllBytes(bundle); bytes[0] ^= 1; File.WriteAllBytes(bundle, bytes); }
            Assert.AreEqual("DeploymentBundleMismatch", Assert.Throws<AssetException>(() => AssetDeployment.CopyToStreamingAssets(_source)).Code);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(DeploymentFiles.Destination, "catalog.json")));
            DeploymentSnapshot.ValidateDeployed();
        }

        [Test]
        public void EvidenceDamageAndUnexpectedRuntimeFilesBlockPlayerBuild()
        {
            AssetBuilder.Build(_settings, _source);
            AssetDeployment.CopyToStreamingAssets(_source);
            string snapshot = DeploymentSnapshot.ValidateDeployed();
            string link = Path.Combine(snapshot, "link.xml");
            byte[] original = File.ReadAllBytes(link);
            try
            {
                File.AppendAllText(link, "broken");
                Assert.AreEqual("DeploymentEvidenceMismatch", Assert.Throws<AssetException>(() => DeploymentSnapshot.ValidateDeployed()).Code);
            }
            finally { File.WriteAllBytes(link, original); }
            File.AppendAllText(link, "broken again");
            AssetDeployment.CopyToStreamingAssets(_source);
            DeploymentSnapshot.ValidateDeployed();
            File.WriteAllText(Path.Combine(DeploymentFiles.Destination, "build-report.txt"), "not runtime");
            Assert.AreEqual("UnexpectedDeploymentFile", Assert.Throws<AssetException>(() => DeploymentSnapshot.ValidateDeployed()).Code);
        }

        [Test]
        public void ReplacementFailureRollsBackOldDirectory()
        {
            string target = Path.Combine(_root, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "old"), "keep");
            Assert.Throws<DirectoryNotFoundException>(() => DeploymentFiles.ReplaceDirectory(Path.Combine(_root, "missing-staged"), target, Path.Combine(_root, "backup")));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "old")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "backup")));
        }

        [Test]
        public void OverlapAndLockedSourceAreRejectedWithoutReplacingContent()
        {
            Assert.AreEqual("DeploymentPathOverlap", Assert.Throws<AssetException>(() => AssetDeployment.CopyToStreamingAssets(DeploymentFiles.Destination)).Code);
            AssetBuilder.Build(_settings, _source);
            AssetDeployment.CopyToStreamingAssets(_source);
            using var locked = File.Open(Path.Combine(_source, "catalog.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => AssetDeployment.CopyToStreamingAssets(_source));
            DeploymentFiles.ValidateRuntime(DeploymentFiles.Destination, true);
        }

        [Test]
        public void LinkedOldContentIsRejectedWithoutTouchingExternalDirectory()
        {
            AssetBuilder.Build(_settings, _source);
            Directory.CreateDirectory(DeploymentFiles.Destination);
            string external = Path.Combine(_root, "external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep"), "outside");
            string junction = Path.Combine(DeploymentFiles.Destination, "junction");
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = "/c mklink /J \"" + junction + "\" \"" + external + "\"",
                UseShellExecute = false, CreateNoWindow = true
            });
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode);
            try
            {
                Assert.AreEqual("DeploymentPathLink", Assert.Throws<AssetException>(() => AssetDeployment.CopyToStreamingAssets(_source)).Code);
                Assert.AreEqual("outside", File.ReadAllText(Path.Combine(external, "keep")));
                Assert.IsTrue(Directory.Exists(junction));
            }
            finally { Directory.Delete(junction); }
        }
    }
}
