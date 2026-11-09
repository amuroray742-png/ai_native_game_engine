using System;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class AssetBuildTests
    {
        [Test]
        public void ErrorContextPreservesFailedDependencyBundle()
        {
            var original = new AssetException("BundleFileInvalid", "missing", bundle: "shared", stage: "bundle");
            var error = (AssetException)AssetException.WithContext(original, "prefab-address", "prefab-group", "asset");
            Assert.AreEqual("prefab-address", error.Address);
            Assert.AreEqual("shared", error.Bundle);
            Assert.AreEqual("bundle", error.Stage);
            Assert.AreEqual(original.Code, error.Code);
            Assert.AreSame(original, error.InnerException);
        }
        [Test]
        public void SharedDependenciesHaveOneOwnerAndStablePacking()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            Assert.NotNull(settings, "Run AssetsDemoSetup.Create in the demonstration project.");
            var plan = AssetBuildPlan.Create(settings);
            var material = plan.Owners["Assets/Probe/SharedMaterial.mat"];
            var texture = plan.Owners["Assets/Probe/SharedTexture.asset"];
            Assert.AreEqual(material, texture);
            StringAssert.StartsWith("shared_", material);
            var copy = UnityEngine.Object.Instantiate(settings);
            try
            {
                Array.Reverse(copy.Groups);
                CollectionAssert.AreEqual(plan.Owners, AssetBuildPlan.Create(copy).Owners);
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }
        [Test]
        public void DuplicateAddressFailsBeforeBuild()
        {
            var settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset"));
            try
            {
                settings.Groups[1].Assets[0].Address = settings.Groups[0].Assets[0].Address;
                Assert.Throws<AssetException>(() => AssetBuildPlan.Create(settings));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }
        [Test]
        public void InvalidCatalogAndCyclesAreRejected()
        {
            var catalog = new AssetCatalog { UnityVersion = Application.unityVersion, Bundles = new[] {
                new BundleEntry { Name = "a", Dependencies = new[] { "b" } }, new BundleEntry { Name = "b", Dependencies = new[] { "a" } } } };
            Assert.Throws<AssetException>(() => catalog.Validate());
            catalog.Bundles = new[] { new BundleEntry { Name = "../a" } };
            Assert.Throws<AssetException>(() => catalog.Validate());
        }
    }
}
