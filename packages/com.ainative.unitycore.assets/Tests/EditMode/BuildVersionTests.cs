using System;
using System.IO;
using System.Text.RegularExpressions;
using AiNative.UnityCore.Assets.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class BuildVersionTests
    {
        private AssetGroups _settings;
        private string _root;
        private string _assetPath;

        [SetUp]
        public void SetUp()
        {
            _settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"));
            _root = Path.GetFullPath("BuildArtifacts/VersionTests/" + Guid.NewGuid().ToString("N"));
            _settings.Version = 1;
            _settings.OutputDirectory = _root + "/{version}";
            _assetPath = "Assets/VersionTest-" + Guid.NewGuid().ToString("N") + ".asset";
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_assetPath))
            {
                File.SetAttributes(_assetPath, FileAttributes.Normal);
                AssetDatabase.DeleteAsset(_assetPath);
            }
            else if (_settings != null) { UnityEngine.Object.DestroyImmediate(_settings); }
        }

        [Test]
        public void OldSerializedConfigurationUsesDefaultsWithoutRewriting()
        {
            var defaults = ScriptableObject.CreateInstance<AssetGroups>();
            try
            {
                Assert.AreEqual(1, defaults.Version);
                Assert.AreEqual("{projectDir}/BundleBuildOutput/{version}", defaults.OutputDirectory);
            }
            finally { UnityEngine.Object.DestroyImmediate(defaults); }
            AssetDatabase.CreateAsset(_settings, _assetPath);
            string yaml = File.ReadAllText(_assetPath);
            yaml = Regex.Replace(yaml, @"^  (Version|OutputDirectory):.*\r?\n", "", RegexOptions.Multiline);
            File.WriteAllText(_assetPath, yaml);
            AssetDatabase.ImportAsset(_assetPath, ImportAssetOptions.ForceUpdate);
            _settings = AssetDatabase.LoadAssetAtPath<AssetGroups>(_assetPath);
            Assert.AreEqual(1, _settings.Version);
            Assert.AreEqual("{projectDir}/BundleBuildOutput/{version}", _settings.OutputDirectory);
            Assert.AreEqual(yaml, File.ReadAllText(_assetPath));
        }

        [TestCase("{projectDir}/BundleBuildOutput/{version}")]
        [TestCase("BundleBuildOutput/{version}")]
        public void TemplatesResolveAgainstUnityProject(string template)
        {
            _settings.Version = 42;
            _settings.OutputDirectory = template;
            Assert.AreEqual(Path.GetFullPath(Path.Combine(Application.dataPath, "../BundleBuildOutput/42")), AssetBuildSession.ResolveOutputDirectory(_settings));
        }

        [TestCase("")]
        [TestCase("BundleBuildOutput")]
        [TestCase("{ProjectDir}/{version}")]
        [TestCase("{projectDir}/{Version}")]
        [TestCase("{unknown}/{version}")]
        [TestCase("{version}/bad{")]
        public void InvalidTemplatesFailBeforeBuild(string template)
        {
            _settings.OutputDirectory = template;
            Assert.Throws<ArgumentException>(() => AssetBuilder.Build(_settings));
            Assert.AreEqual(1, _settings.Version);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        public void InvalidVersionsFailBeforeBuild(int version)
        {
            _settings.Version = version;
            Assert.Throws<ArgumentOutOfRangeException>(() => AssetBuilder.Build(_settings, _root));
            Assert.AreEqual(version, _settings.Version);
            Assert.IsFalse(Directory.Exists(_root));
        }

        [TestCase(AssetBuildBackend.Builtin)]
        [TestCase(AssetBuildBackend.ScriptableBuildPipeline)]
        public void ConsecutiveBuildsPublishAndPersistNextVersion(AssetBuildBackend backend)
        {
            _settings.BuildBackend = backend;
            AssetDatabase.CreateAsset(_settings, _assetPath);
            for (int version = 1; version <= 2; version++)
            {
                AssetBuilder.Build(_settings, new AssetBuildOptions { UseCache = version == 1 });
                string output = Path.Combine(_root, version.ToString());
                var metadata = JsonUtility.FromJson<AssetBuildMetadata>(File.ReadAllText(Path.Combine(output, "build-metadata.json")));
                Assert.AreEqual(version, metadata.ContentVersion);
                string metadataPath = Path.Combine(output, "build-metadata.json");
                string legacy = Regex.Replace(File.ReadAllText(metadataPath), @"\s*""ContentVersion"":\s*\d+,", "");
                File.WriteAllText(metadataPath, legacy);
                AssetPlayerBuildSupport.ValidatePreservation(output, out var legacyMetadata);
                Assert.AreEqual(0, legacyMetadata.ContentVersion);
                File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));
                Assert.AreEqual(version + 1, _settings.Version);
                Assert.IsFalse(EditorUtility.IsDirty(_settings));
                StringAssert.Contains("  Version: " + (version + 1), File.ReadAllText(_assetPath));
                Resources.UnloadAsset(_settings);
                _settings = AssetDatabase.LoadAssetAtPath<AssetGroups>(_assetPath);
                Assert.AreEqual(version + 1, _settings.Version);
                Assert.AreEqual(version, _settings.LastBuildVersion);
                Assert.AreEqual(output, DeploymentFiles.FullPath(_settings.LastBuildPath));
                Assert.AreEqual(AssetBuilder.FileHash(Path.Combine(output, "catalog.json")), _settings.LastBuildCatalogSha256);
            }
        }

        [Test]
        public void ExplicitOutputOverridesTemplateAndTransientVersionOnlyChangesInMemory()
        {
            _settings.OutputDirectory = "invalid template";
            AssetBuilder.Build(_settings, _root);
            Assert.AreEqual(2, _settings.Version);
            Assert.AreEqual("", AssetDatabase.GetAssetPath(_settings));
            Assert.AreEqual(1, JsonUtility.FromJson<AssetBuildMetadata>(File.ReadAllText(Path.Combine(_root, "build-metadata.json"))).ContentVersion);
            Assert.Throws<IOException>(() => AssetBuilder.Build(_settings, _root));
            Assert.AreEqual(2, _settings.Version);
        }

        [Test]
        public void ValidationAndPublicationFailuresDoNotAdvanceVersion()
        {
            var groups = _settings.Groups;
            _settings.Groups = new[] { new ResourceGroup { Id = "" } };
            Assert.Catch(() => AssetBuilder.Build(_settings));
            Assert.AreEqual(1, _settings.Version);
            _settings.Groups = groups;
            Directory.CreateDirectory(_root);
            string blocker = Path.Combine(_root, "parent-file");
            File.WriteAllText(blocker, "keep");
            Assert.Catch<IOException>(() => AssetBuilder.Build(_settings, Path.Combine(blocker, "child")));
            Assert.AreEqual(1, _settings.Version);
            Assert.AreEqual("keep", File.ReadAllText(blocker));
        }

        [Test]
        public void SessionRejectsReentryAndReleasesRegistrationAfterFailure()
        {
            using (AssetBuildSession.Begin(_settings, _root))
            {
                Assert.Throws<InvalidOperationException>(() => AssetBuilder.Build(_settings, _root));
            }
            using (var retry = AssetBuildSession.Begin(_settings, _root)) { Assert.AreEqual(1, retry.Version); }
        }

        [Test]
        public void ReadOnlyConfigurationFailsBeforeBuild()
        {
            AssetDatabase.CreateAsset(_settings, _assetPath);
            File.SetAttributes(_assetPath, FileAttributes.ReadOnly);
            Assert.Throws<IOException>(() => AssetBuilder.Build(_settings));
            Assert.AreEqual(1, _settings.Version);
            Assert.IsFalse(Directory.Exists(_root));
        }

        [Test]
        public void BackendFailureDoesNotAdvanceVersionAndReleasesSession()
        {
            var callbacks = ContentPipeline.BuildCallbacks;
            bool reachedBackend = false;
            ContentPipeline.BuildCallbacks = new BuildCallbacks
            {
                PostDependencyCallback = (parameters, dependencies) =>
                {
                    reachedBackend = true;
                    return ReturnCode.Canceled;
                }
            };
            try
            {
                _settings.BuildBackend = AssetBuildBackend.ScriptableBuildPipeline;
                Assert.AreEqual("SbpBuildFailed", Assert.Throws<AssetException>(() => AssetBuilder.Build(_settings)).Code);
                Assert.IsTrue(reachedBackend);
                Assert.AreEqual(1, _settings.Version);
                Assert.IsFalse(Directory.Exists(_root));
                using (AssetBuildSession.Begin(_settings, _root)) { }
            }
            finally { ContentPipeline.BuildCallbacks = callbacks; }
        }

        [Test]
        public void VersionPersistenceDoesNotSaveOtherDirtyAssets()
        {
            AssetDatabase.CreateAsset(_settings, _assetPath);
            string otherPath = _assetPath.Replace(".asset", "-other.asset");
            var other = ScriptableObject.CreateInstance<AssetGroups>();
            AssetDatabase.CreateAsset(other, otherPath);
            try
            {
                string saved = File.ReadAllText(otherPath);
                other.Version = 50;
                EditorUtility.SetDirty(other);
                using (var session = AssetBuildSession.Begin(_settings, _root)) { session.Complete(new string('a', 64)); }
                Assert.IsTrue(EditorUtility.IsDirty(other));
                Assert.AreEqual(saved, File.ReadAllText(otherPath));
                Assert.IsFalse(EditorUtility.IsDirty(_settings));
            }
            finally { AssetDatabase.DeleteAsset(otherPath); }
        }

        [Test]
        public void SaveFailureAfterPublicationKeepsContentAndNextVersion()
        {
            AssetDatabase.CreateAsset(_settings, _assetPath);
            using var session = AssetBuildSession.Begin(_settings, _root);
            string staging = _root + "-staging";
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "content"), "published");
            AssetBuilder.Publish(staging, _root);
            File.SetAttributes(_assetPath, FileAttributes.ReadOnly);
            var error = Assert.Throws<IOException>(() => session.Complete(new string('a', 64)));
            StringAssert.Contains("published to " + _root, error.Message);
            Assert.AreEqual(2, _settings.Version);
            Assert.IsTrue(EditorUtility.IsDirty(_settings));
            Assert.AreEqual("published", File.ReadAllText(Path.Combine(_root, "content")));
            File.SetAttributes(_assetPath, FileAttributes.Normal);
            AssetDatabase.SaveAssetIfDirty(_settings);
            StringAssert.Contains("  Version: 2", File.ReadAllText(_assetPath));
        }
    }
}
