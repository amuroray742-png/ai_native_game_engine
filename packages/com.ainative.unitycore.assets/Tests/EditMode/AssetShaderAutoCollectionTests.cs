using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.UnityCore.Assets.Editor.Tests
{
    public sealed class AssetShaderAutoCollectionTests
    {
        private string Root;
        private bool _ownsRecording;
        private AssetGroups _groups;
        private AssetShaderSettings _settings;
        private Shader _shader;

        [SetUp]
        public void SetUp()
        {
            Root = null;
            _ownsRecording = false;
            if (AssetShaderRecording.IsRecording) { Assert.Ignore("A user shader recording is active."); }
            string folder = "AutoCollectionTests-" + Guid.NewGuid().ToString("N");
            Root = "Assets/" + folder;
            AssetDatabase.CreateFolder("Assets", folder);
            _shader = Shader.Find("AI Native/Keyword Probe");
            if (_shader == null) { Assert.Ignore("AssetsDemo URP probe is required for this fixture."); }
            _settings = ScriptableObject.CreateInstance<AssetShaderSettings>();
            _groups = ScriptableObject.CreateInstance<AssetGroups>();
            _groups.Shaders = _settings;
            var material = new Material(_shader);
            AssetDatabase.CreateAsset(material, Root + "/Material.mat");
            _groups.Groups = new[] { new ResourceGroup { Id = "Auto", Assets = new[] { new ResourceAddress { Asset = material } } } };
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownsRecording) { AssetShaderRecording.Cancel(); }
            UnityEngine.Object.DestroyImmediate(_groups);
            if (_settings != null && !AssetDatabase.Contains(_settings)) { UnityEngine.Object.DestroyImmediate(_settings); }
            if (Root != null) { AssetDatabase.DeleteAsset(Root); }
        }

        [Test]
        public void StaticScanGeneratesWithoutManualDeclarations()
        {
            var collection = AssetShaderAutoCollection.Generate(_groups, Root + "/Static.shadervariants");
            Assert.IsTrue(collection.Contains(Variant()));
            Assert.IsFalse(collection.Contains(Variant("PROBE_GREEN")));
            Assert.IsTrue(File.Exists(Root + "/Static.shadervariants.generation.json"));
            Assert.IsEmpty(_settings.RequiredVariants);
            string occupied = Root + "/Occupied.shadervariants";
            Directory.CreateDirectory(occupied + ".generation.json");
            Assert.Throws<IOException>(() => AssetShaderAutoCollection.Generate(_groups, occupied));
            Assert.IsTrue(Directory.Exists(occupied + ".generation.json"));
        }

        [Test]
        public void RecordingsAndManualDeclarationsMergeAndDeduplicate()
        {
            var recording = new ShaderVariantCollection();
            recording.Add(Variant());
            recording.Add(Variant("PROBE_GREEN"));
            AssetDatabase.CreateAsset(recording, Root + "/Recorded.shadervariants");
            _settings.RuntimeCollections = new[] { recording, recording };
            _settings.RequiredVariants = new[] { new AssetShaderVariant { Shader = _shader,
                PassType = PassType.ScriptableRenderPipelineDefaultUnlit, Keywords = new[] { "PROBE_GREEN", "PROBE_GREEN" } } };
            var collection = AssetShaderAutoCollection.Generate(_groups, Root + "/Merged.shadervariants");
            Assert.AreEqual(2, collection.variantCount);
            Assert.IsTrue(collection.Contains(Variant("PROBE_GREEN")));
            Assert.Throws<IOException>(() => AssetShaderAutoCollection.Generate(_groups, Root + "/Merged.shadervariants"));
        }

        [Test]
        public void AutoConflictFailsWithoutPublishingAnAsset()
        {
            _settings.Exclusions = new[] { new AssetShaderExclusion { Shader = _shader, PassName = "Forward" } };
            Assert.AreEqual("CollectedShaderExcluded", Assert.Throws<AssetException>(() =>
                AssetShaderAutoCollection.Generate(_groups, Root + "/Conflict.shadervariants")).Code);
            Assert.IsFalse(File.Exists(Root + "/Conflict.shadervariants"));
        }

        [Test]
        public void MissingRecordingFailsAndSessionCannotBeNested()
        {
            _settings.RuntimeCollections = new ShaderVariantCollection[] { null };
            Assert.Throws<ArgumentException>(() => AssetShaderAutoCollection.Generate(_groups, Root + "/Missing.shadervariants"));
            Assert.Throws<InvalidOperationException>(() => AssetShaderRecording.Save(Root + "/Capture.shadervariants"));
            AssetDatabase.CreateAsset(_settings, Root + "/Settings.asset");
            AssetShaderRecording.Begin(_settings);
            _ownsRecording = true;
            Assert.IsTrue(AssetShaderRecording.IsRecording);
            Assert.Throws<InvalidOperationException>(() => AssetShaderRecording.Begin(_settings));
            AssetShaderRecording.Cancel();
            Assert.IsFalse(AssetShaderRecording.IsRecording);
        }

        [Test]
        public void StaleRecordedPassIsReportedInsteadOfClaimedAsCoverage()
        {
            var recording = new ShaderVariantCollection();
            recording.Add(Variant("PROBE_GREEN"));
            AssetDatabase.CreateAsset(recording, Root + "/Stale.shadervariants");
            using (var serialized = new SerializedObject(recording))
            {
                serialized.FindProperty("m_Shaders").GetArrayElementAtIndex(0)
                    .FindPropertyRelative("second.variants").GetArrayElementAtIndex(0)
                    .FindPropertyRelative("passType").intValue = (int)PassType.ShadowCaster;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            _settings.RuntimeCollections = new[] { recording };
            var collection = AssetShaderAutoCollection.Generate(_groups, Root + "/Filtered.shadervariants");
            Assert.AreEqual(1, collection.variantCount);
            var report = JsonUtility.FromJson<AssetShaderGenerationReport>(File.ReadAllText(Root + "/Filtered.shadervariants.generation.json"));
            Assert.AreEqual(1, report.RejectedRecordedVariants);
            Assert.IsTrue(report.Notes.Any(x => x.Contains("Rejected recording") && x.Contains("PROBE_GREEN")));
        }

        private ShaderVariantCollection.ShaderVariant Variant(params string[] keywords)
            => new ShaderVariantCollection.ShaderVariant(_shader, PassType.ScriptableRenderPipelineDefaultUnlit, keywords);
    }
}
