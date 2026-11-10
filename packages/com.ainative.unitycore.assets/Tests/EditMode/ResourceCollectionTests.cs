using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class ResourceCollectionTests
    {
        private string _root;
        private AssetGroups _settings;
        private ResourceGroup _group;

        [SetUp]
        public void SetUp()
        {
            _root = "Assets/Collection-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_root));
            AssetDatabase.CreateFolder(_root, "Nested");
            AssetDatabase.CreateAsset(new Texture2D(1, 1), _root + "/A.asset");
            AssetDatabase.CreateAsset(new Texture2D(1, 1), _root + "/Nested/B.asset");
            _settings = ScriptableObject.CreateInstance<AssetGroups>();
            _group = new ResourceGroup { Id = "stable-id", Name = "A",
                Folders = new[] { AssetDatabase.LoadMainAssetAtPath(_root) } };
            _settings.Groups = new[] { _group };
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_settings);
            AssetDatabase.DeleteAsset(_root);
        }

        [Test]
        public void RecursiveFoldersAndExplicitEntriesShareGlobalAddressMode()
        {
            _group.Folders = _group.Folders.Concat(new[] { AssetDatabase.LoadMainAssetAtPath(_root + "/Nested") }).ToArray();
            _group.Assets = new[] { new ResourceAddress { Asset = AssetDatabase.LoadMainAssetAtPath(_root + "/A.asset") } };
            CollectionAssert.AreEquivalent(new[] { _root + "/A", _root + "/Nested/B" }, AssetBuildPlan.Create(_settings).Addresses.Select(x => x.Address));
            AssetDatabase.CreateAsset(new Texture2D(1, 1), _root + "/Nested/C.asset");
            Assert.AreEqual(3, AssetBuildPlan.Create(_settings).Addresses.Length);
            _settings.AddressMode = ResourceAddressMode.GroupAndAssetName;
            CollectionAssert.AreEquivalent(new[] { "A/A", "A/B", "A/C" }, AssetBuildPlan.Create(_settings).Addresses.Select(x => x.Address));
        }

        [Test]
        public void UnknownFileIsNotCollectedAsBundleSource()
        {
            string path = _root + "/notes.unsupported";
            File.WriteAllText(path, "not a Unity imported resource");
            AssetDatabase.ImportAsset(path);
            Assert.AreEqual(typeof(DefaultAsset), AssetDatabase.GetMainAssetTypeAtPath(path));
            Assert.IsFalse(AssetBuildPlan.Create(_settings).Owners.ContainsKey(path));
            string text = _root + "/readme.txt";
            File.WriteAllText(text, "a supported TextAsset");
            AssetDatabase.ImportAsset(text);
            string output = Path.GetFullPath("BuildArtifacts/FolderFilter-" + Guid.NewGuid().ToString("N"));
            var catalog = AssetBuilder.Build(_settings, output);
            Assert.IsTrue(catalog.Assets.Any(x => x.Path == text));
            Assert.IsFalse(catalog.Assets.Any(x => x.Path == path));
            StringAssert.Contains(path, File.ReadAllText(Path.Combine(output, "excluded.txt")));
            _group.Assets = new[] { new ResourceAddress { Asset = AssetDatabase.LoadMainAssetAtPath(path) } };
            Assert.AreEqual("InvalidEntry", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
        }

        [Test]
        public void ExtensionRemovalCollisionReportsBothSourcePaths()
        {
            string text = _root + "/A.txt";
            File.WriteAllText(text, "same address, different source GUID");
            AssetDatabase.ImportAsset(text);
            var error = Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings));
            Assert.AreEqual("InvalidEntry", error.Code);
            Assert.AreEqual(_root + "/A", error.Address);
            StringAssert.Contains(text, error.Message);
            StringAssert.Contains(_root + "/A.asset", error.Message);
        }

        [Test]
        public void FolderOrderAndMovesRecomputeDeterministicAddresses()
        {
            _group.Folders = new[] { AssetDatabase.LoadMainAssetAtPath(_root + "/Nested"), AssetDatabase.LoadMainAssetAtPath(_root) };
            var before = AssetBuildPlan.Create(_settings);
            Array.Reverse(_group.Folders);
            CollectionAssert.AreEqual(before.Owners, AssetBuildPlan.Create(_settings).Owners);
            Assert.AreEqual("", AssetDatabase.MoveAsset(_root + "/Nested/B.asset", _root + "/Moved.asset"));
            var moved = AssetBuildPlan.Create(_settings);
            CollectionAssert.AreEquivalent(new[] { _root + "/A", _root + "/Moved" }, moved.Addresses.Select(x => x.Address));
            Assert.AreEqual(before.Owners[_root + "/Nested/B.asset"], moved.Owners[_root + "/Moved.asset"]);
        }

        [Test]
        public void EmptyConfigurationStillValidatesGlobalMode()
        {
            _settings.Groups = Array.Empty<ResourceGroup>();
            _settings.AddressMode = (ResourceAddressMode)99;
            Assert.AreEqual("InvalidAddressMode", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
        }

        [Test]
        public void GlobalModeAppliesToAllGroupsAndIgnoresLegacyManualAddresses()
        {
            // Old serialized group modes and addresses must not override the new global policy.
            JsonUtility.FromJsonOverwrite("{\"Groups\":[{\"Id\":\"first\",\"AddressMode\":0,\"Assets\":[{\"Address\":\"legacy\"}]},{\"Id\":\"second\",\"AddressMode\":2,\"Assets\":[{\"Address\":\"other\"}]}]}", _settings);
            _settings.Groups[0].Assets[0].Asset = AssetDatabase.LoadMainAssetAtPath(_root + "/A.asset");
            _settings.Groups[1].Assets[0].Asset = AssetDatabase.LoadMainAssetAtPath(_root + "/Nested/B.asset");
            CollectionAssert.AreEquivalent(new[] { _root + "/A", _root + "/Nested/B" }, AssetBuildPlan.Create(_settings).Addresses.Select(x => x.Address));
            _settings.AddressMode = ResourceAddressMode.GroupAndAssetName;
            CollectionAssert.AreEquivalent(new[] { "first/A", "second/B" }, AssetBuildPlan.Create(_settings).Addresses.Select(x => x.Address));
        }

        [Test]
        public void AmbiguousAddressesAndCrossGroupOwnershipAreRejected()
        {
            AssetDatabase.CreateAsset(new Texture2D(1, 1), _root + "/Nested/A.asset");
            _settings.AddressMode = ResourceAddressMode.GroupAndAssetName;
            Assert.AreEqual("InvalidEntry", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
            _settings.AddressMode = ResourceAddressMode.FullPath;
            _settings.Groups = new[] { _group, new ResourceGroup { Id = "B", Folders = _group.Folders } };
            var error = Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings));
            Assert.AreEqual("InvalidEntry", error.Code);
            StringAssert.Contains("stable-id", error.Message);
            StringAssert.Contains("B", error.Message);
        }

        [Test]
        public void UnknownGlobalModeAndNonFolderReferencesAreRejected()
        {
            _settings.AddressMode = (ResourceAddressMode)99;
            Assert.AreEqual("InvalidAddressMode", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
            _settings.AddressMode = ResourceAddressMode.FullPath;
            _group.Folders = new[] { AssetDatabase.LoadMainAssetAtPath(_root + "/A.asset") };
            Assert.AreEqual("InvalidFolder", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
        }

        [Test]
        public void SceneDependencyNameCannotAliasAnotherSharedGroupSet()
        {
            var first = AssetDatabase.LoadAssetAtPath<Texture2D>(_root + "/A.asset");
            Material Material(string name, Texture2D texture)
            {
                var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = texture };
                AssetDatabase.CreateAsset(material, _root + "/" + name + ".mat");
                return material;
            }
            var a = Material("first", first);
            var b = Material("second", first);
            string path = _root + "/Content.unity";
            // Copy the saved fixture so this test never replaces or saves the user's open scene.
            Assert.IsTrue(AssetDatabase.CopyAsset("Assets/Probe/Content.unity", path));
            AssetDatabase.SaveAssets();
            // A directory with scenes and ordinary assets must still respect the scene-only boundary.
            Assert.AreEqual("MixedSceneGroup", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
            _settings.Groups = new[] {
                new ResourceGroup { Id = "A", Assets = new[] { new ResourceAddress { Asset = a } } },
                new ResourceGroup { Id = "B", Assets = new[] { new ResourceAddress { Asset = b } } },
                new ResourceGroup { Id = "A_B", Assets = new[] { new ResourceAddress { Asset = AssetDatabase.LoadMainAssetAtPath(path) } } }
            };
            _settings.BundleNaming = BundleNamingMode.GroupName;
            var failure = Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings));
            Assert.AreEqual("BundleNameCollision", failure.Code);
            StringAssert.Contains("share_a_b.bundle", failure.Message);
            // Stable hashes distinguish the single scene group from the two-group reference set.
            _settings.BundleNaming = BundleNamingMode.StableHash;
            Assert.DoesNotThrow(() => AssetBuildPlan.Create(_settings));
        }

        [Test]
        public void SharedNameConcatenationCollisionIsRejected()
        {
            var first = AssetDatabase.LoadAssetAtPath<Texture2D>(_root + "/A.asset");
            var second = AssetDatabase.LoadAssetAtPath<Texture2D>(_root + "/Nested/B.asset");
            ResourceGroup Group(string name, Texture2D texture)
            {
                var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = texture };
                AssetDatabase.CreateAsset(material, _root + "/" + name + ".mat");
                return new ResourceGroup { Id = name, Assets = new[] { new ResourceAddress { Asset = material } } };
            }
            _settings.Groups = new[] { Group("A_B", first), Group("C", first), Group("A", second), Group("B_C", second) };
            _settings.BundleNaming = BundleNamingMode.GroupName;
            AssetDatabase.SaveAssets();
            Assert.AreEqual("BundleNameCollision", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
        }

        [Test]
        public void ReadableSharedNamesAreStableAndActuallyBuild()
        {
            var shared = AssetDatabase.LoadAssetAtPath<Texture2D>(_root + "/A.asset");
            ResourceGroup Group(string id, string name)
            {
                var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = shared };
                AssetDatabase.CreateAsset(material, _root + "/" + id + ".mat");
                return new ResourceGroup { Id = id, Name = name,
                    Assets = new[] { new ResourceAddress { Asset = material } } };
            }
            _settings.Groups = new[] { Group("id-b", "B"), Group("id-a", "A") };
            _settings.BundleNaming = BundleNamingMode.GroupName;
            AssetDatabase.SaveAssets();
            var plan = AssetBuildPlan.Create(_settings);
            Assert.AreEqual("share_a_b.bundle", plan.Owners[_root + "/A.asset"]);
            Assert.AreEqual("group_a.bundle", plan.Owners[_root + "/id-a.mat"]);
            Array.Reverse(_settings.Groups);
            CollectionAssert.AreEqual(plan.Owners, AssetBuildPlan.Create(_settings).Owners);
            string output = Path.GetFullPath("BuildArtifacts/Readable-" + Guid.NewGuid().ToString("N"));
            AssetBuilder.Build(_settings, output);
            Assert.IsTrue(File.Exists(Path.Combine(output, "share_a_b.bundle")));
            _settings.Groups[1].Name = "a";
            Assert.AreEqual("BundleNameCollision", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
            _settings.Groups[1].Name = "../bad";
            Assert.AreEqual("InvalidBundleName", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
        }
    }
}
