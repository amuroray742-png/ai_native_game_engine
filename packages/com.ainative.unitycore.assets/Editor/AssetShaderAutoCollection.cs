using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.UnityCore.Assets.Editor
{
    [Serializable]
    public sealed class AssetShaderGenerationReport
    {
        public int FormatVersion = 1;
        public string UnityVersion = Application.unityVersion;
        public int MaterialVariants;
        public int RecordedVariants;
        public int RejectedRecordedVariants;
        public int ManualVariants;
        public int Duplicates;
        public int Variants;
        public string[] Notes;
    }

    // Editor-only producer. No URP dependency and no shader stripping whitelist is created.
    public static class AssetShaderAutoCollection
    {
        public static ShaderVariantCollection Generate(AssetGroups groups, string assetPath)
        {
            if (groups == null || groups.Shaders == null)
            {
                throw new ArgumentException("Assign Shader Settings to the resource groups first.");
            }
            ValidateNewPath(assetPath);
            AssetShaderBuildSession.Validate(groups.Shaders);
            var notes = new List<string>();
            var report = new AssetShaderGenerationReport();
            var collection = new ShaderVariantCollection();
            bool writingReport = false;
            try
            {
                var plan = AssetBuildPlan.Create(groups);
                foreach (string path in plan.Owners.Keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(SceneAsset)) { continue; }
                    foreach (var material in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
                    {
                        int accepted = 0;
                        string[] keywords = Normalize(material.shaderKeywords);
                        // The native SVC constructor validates the pass type against the actual shader.
                        // Testing its finite PassType enum avoids guessing from URP shader names or LightMode.
                        foreach (PassType pass in Enum.GetValues(typeof(PassType)))
                        {
                            ShaderVariantCollection.ShaderVariant variant;
                            try { variant = new ShaderVariantCollection.ShaderVariant(material.shader, pass, keywords); }
                            catch (ArgumentException) { continue; }
                            Add(collection, variant, groups.Shaders, report);
                            report.MaterialVariants++;
                            accepted++;
                        }
                        if (accepted == 0) { notes.Add("No valid static variant: " + path + "/" + material.name); }
                    }
                }
                foreach (var recording in groups.Shaders.RuntimeCollections)
                {
                    if (recording == null) { throw new ArgumentException("Runtime Collections contains a missing collection."); }
                    foreach (var variant in Read(recording))
                    {
                        report.RecordedVariants++;
                        ShaderVariantCollection.ShaderVariant validated;
                        try { validated = new ShaderVariantCollection.ShaderVariant(variant.shader, variant.passType, variant.keywords); }
                        catch (ArgumentException error)
                        {
                            report.RejectedRecordedVariants++;
                            notes.Add("Rejected recording " + AssetDatabase.GetAssetPath(recording) + ": " + variant.shader.name +
                                "/" + variant.passType + "/" + string.Join(" ", variant.keywords) + ": " + error.Message);
                            continue;
                        }
                        Add(collection, validated, groups.Shaders, report);
                    }
                }
                foreach (var declaration in groups.Shaders.RequiredVariants)
                {
                    Add(collection, new ShaderVariantCollection.ShaderVariant(declaration.Shader,
                        declaration.PassType, Normalize(declaration.Keywords)), groups.Shaders, report);
                    report.ManualVariants++;
                }
                report.Variants = collection.variantCount;
                notes.Add("Static material combinations are candidates, not proof of runtime coverage. Record each renderer/quality configuration you use.");
                notes.Add("Recordings include all Editor rendering since Begin, including Scene view and shaders outside the resource groups.");
                notes.Add("SVC stores PassType, not PassName; variants with identical shader/type/keywords are merged.");
                report.Notes = notes.ToArray();
                AssetDatabase.CreateAsset(collection, assetPath);
                AssetDatabase.SaveAssetIfDirty(collection);
                writingReport = true;
                File.WriteAllText(assetPath + ".generation.json", JsonUtility.ToJson(report, true));
                AssetDatabase.ImportAsset(assetPath + ".generation.json");
                Debug.Log("Generated " + collection.variantCount + " shader variants; rejected recorded combinations: " +
                    report.RejectedRecordedVariants + ". See " + assetPath + ".generation.json");
                return collection;
            }
            catch
            {
                if (writingReport)
                {
                    string reportPath = assetPath + ".generation.json";
                    if (!AssetDatabase.DeleteAsset(reportPath)) { File.Delete(reportPath); }
                }
                if (AssetDatabase.GetAssetPath(collection) == assetPath) { AssetDatabase.DeleteAsset(assetPath); }
                else { UnityEngine.Object.DestroyImmediate(collection); }
                throw;
            }
        }

        internal static void ValidateNewPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                path.Contains("..") || path.Contains('\\') || !path.EndsWith(".shadervariants", StringComparison.Ordinal))
            {
                throw new ArgumentException("Choose a new Assets/*.shadervariants path.");
            }
            if (new[] { path, path + ".meta", path + ".generation.json", path + ".generation.json.meta" }.Any(x => File.Exists(x) || Directory.Exists(x)))
            {
                throw new IOException("Output already exists; choose a new path: " + path);
            }
        }

        private static string[] Normalize(IEnumerable<string> keywords)
            => keywords.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        private static void Add(ShaderVariantCollection collection, ShaderVariantCollection.ShaderVariant variant,
            AssetShaderSettings settings, AssetShaderGenerationReport report)
        {
            // A named rule can remove one of several passes represented by the same SVC entry.
            // Refuse that ambiguous conflict rather than claiming that the whole entry is retained.
            foreach (var rule in settings.Exclusions)
            {
                if (AssetShaderBuildSession.Matches(rule, variant.shader, variant.passType, rule.PassName, variant.keywords))
                {
                    throw new AssetException("CollectedShaderExcluded", "Collected variant conflicts with exclusion: " +
                        variant.shader.name + "/" + variant.passType + "/" + string.Join(" ", variant.keywords));
                }
            }
            if (collection.Contains(variant)) { report.Duplicates++; }
            else if (!collection.Add(variant))
            {
                throw new AssetException("ShaderVariantAddFailed", "Unity refused a validated variant: " + variant.shader.name);
            }
        }

        internal static IEnumerable<ShaderVariantCollection.ShaderVariant> Read(ShaderVariantCollection collection)
        {
            // SVC has no public enumerator. This is the same serialized layout used by Unity's Inspector.
            using var serialized = new SerializedObject(collection);
            var shaders = serialized.FindProperty("m_Shaders");
            if (shaders == null || !shaders.isArray) { throw new NotSupportedException("Unsupported ShaderVariantCollection serialization in " + Application.unityVersion); }
            for (int i = 0; i < shaders.arraySize; i++)
            {
                var entry = shaders.GetArrayElementAtIndex(i);
                var shader = entry.FindPropertyRelative("first").objectReferenceValue as Shader;
                if (shader == null) { throw new ArgumentException("Recording contains a missing Shader: " + collection.name); }
                var variants = entry.FindPropertyRelative("second.variants");
                for (int j = 0; j < variants.arraySize; j++)
                {
                    var variant = variants.GetArrayElementAtIndex(j);
                    yield return new ShaderVariantCollection.ShaderVariant
                    {
                        shader = shader,
                        passType = (PassType)variant.FindPropertyRelative("passType").intValue,
                        keywords = Normalize(variant.FindPropertyRelative("keywords").stringValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    };
                }
            }
        }
    }
}
