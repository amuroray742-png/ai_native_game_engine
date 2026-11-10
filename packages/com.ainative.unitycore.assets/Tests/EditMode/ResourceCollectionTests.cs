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
            _group = new ResourceGroup { Id = "stable-id", Name = "A", AddressMode = ResourceAddressMode.FullPath,
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
        public void RecursiveFoldersRefreshAndExplicitAddressesOverride()
        {
            _group.Folders = _group.Folders.Concat(new[] { AssetDatabase.LoadMainAssetAtPath(_root + "/Nested") }).ToArray();
            _group.Assets = new[] { new ResourceAddress { Address = "custom", Asset = AssetDatabase.LoadMainAssetAtPath(_root + "/A.asset") } };
            CollectionAssert.AreEquivalent(new[] { "custom", _root + "/Nested/B" }, AssetBuildPlan.Create(_settings).Addresses.Select(x => x.Address));
            AssetDatabase.CreateAsset(new Texture2D(1, 1), _root + "/Nested/C.asset");
            Assert.AreEqual(3, AssetBuildPlan.Create(_settings).Addresses.Length);
            _group.AddressMode = ResourceAddressMode.GroupAndAssetName;
            CollectionAssert.AreEquivalent(new[] { "custom", "A/B", "A/C" }, AssetBuildPlan.Create(_settings).Addresses.Select(x => x.Address));
        }

        [Test]
        public void AmbiguousAddressesAndCrossGroupOwnershipAreRejected()
        {
            AssetDatabase.CreateAsset(new Texture2D(1, 1), _root + "/Nested/A.asset");
            _group.AddressMode = ResourceAddressMode.GroupAndAssetName;
            Assert.AreEqual("InvalidEntry", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
            _group.AddressMode = ResourceAddressMode.FullPath;
            _settings.Groups = new[] { _group, new ResourceGroup { Id = "B", AddressMode = ResourceAddressMode.GroupAndAssetName, Folders = _group.Folders } };
            Assert.AreEqual("InvalidEntry", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
        }

        [Test]
        public void ManualFoldersAndNonFolderReferencesAreRejected()
        {
            _group.AddressMode = ResourceAddressMode.Manual;
            Assert.AreEqual("FolderAddressMode", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
            _group.AddressMode = ResourceAddressMode.FullPath;
            _group.Folders = new[] { AssetDatabase.LoadMainAssetAtPath(_root + "/A.asset") };
            Assert.AreEqual("InvalidFolder", Assert.Throws<AssetException>(() => AssetBuildPlan.Create(_settings)).Code);
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
                return new ResourceGroup { Id = name, Assets = new[] { new ResourceAddress { Address = name, Asset = material } } };
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
                return new ResourceGroup { Id = id, Name = name, AddressMode = ResourceAddressMode.GroupAndAssetName,
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
