using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using System.Security.Cryptography;

namespace AiNative.UnityCore.Assets.Editor
{
    [Serializable]
    public sealed class AssetShaderCandidate
    {
        public string Source;
        public string ShaderGuid;
        public string ShaderPath;
        public string[] Keywords;
        public string Origin;
    }

    [Serializable]
    public sealed class AssetShaderCollectionReport
    {
        public int FormatVersion = 1;
        public string UnityVersion;
        public string[] GraphicsApis;
        public string[] PackageVersions;
        public string[] PipelineFingerprints;
        public AssetShaderCandidate[] Candidates;
    }

    public static class AssetShaderCollection
    {
        public static AssetShaderCollectionReport Collect(AssetGroups groups)
            => Collect(groups, AssetBuildPlan.Create(groups));

        internal static AssetShaderCollectionReport Collect(AssetGroups groups, AssetBuildPlan plan)
        {
            var candidates = new List<AssetShaderCandidate>();
            var fingerprints = new SortedSet<string>(StringComparer.Ordinal);
            void Add(Shader shader, string source, string origin, string[] keywords)
            {
                if (shader == null) { return; }
                string path = AssetDatabase.GetAssetPath(shader);
                candidates.Add(new AssetShaderCandidate { Source = source, ShaderGuid = AssetDatabase.AssetPathToGUID(path),
                    ShaderPath = path, Keywords = keywords.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Origin = origin });
            }
            foreach (string path in plan.Owners.Keys)
            {
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(SceneAsset)) { continue; }
                foreach (var material in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
                { Add(material.shader, path, "material", material.shaderKeywords); }
                var shader = AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(Shader) ? AssetDatabase.LoadAssetAtPath<Shader>(path) : null;
                if (shader != null) { Add(shader, path, "resource-dependency", Array.Empty<string>()); }
            }
            var pipelines = new List<RenderPipelineAsset> { GraphicsSettings.defaultRenderPipeline };
            foreach (int quality in QualitySettings.GetActiveQualityLevelsForPlatform("Standalone"))
            { pipelines.Add(QualitySettings.GetRenderPipelineAssetAt(quality)); }
            if (groups.Shaders != null) { pipelines.AddRange(groups.Shaders.AdditionalPipelines); }
            foreach (var pipeline in pipelines.Where(x => x != null).Distinct())
            {
                string path = AssetDatabase.GetAssetPath(pipeline);
                foreach (string dependency in AssetDatabase.GetDependencies(path, true).OrderBy(x => x, StringComparer.Ordinal))
                {
                    fingerprints.Add(AssetDatabase.AssetPathToGUID(dependency) + "\t" + dependency + "\t" + AssetDatabase.GetAssetDependencyHash(dependency));
                    if (AssetDatabase.GetMainAssetTypeAtPath(dependency) == typeof(Shader))
                    { Add(AssetDatabase.LoadAssetAtPath<Shader>(dependency), dependency, "pipeline-dependency", Array.Empty<string>()); }
                }
            }
            foreach (string guid in AssetDatabase.FindAssets("t:RenderPipelineGlobalSettings"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                fingerprints.Add("global-settings\t" + guid + "\t" + path + "\t" + AssetDatabase.GetAssetDependencyHash(path));
            }
            foreach (string path in new[] { "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/QualitySettings.asset" })
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                fingerprints.Add(path + "\t" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
            }
            if (groups.Shaders != null)
            {
                foreach (var shader in groups.Shaders.AdditionalShaders) { Add(shader, "explicit", "user", Array.Empty<string>()); }
                foreach (var variant in groups.Shaders.RequiredVariants) { Add(variant.Shader, variant.PassType + "/" + variant.PassName, "required", variant.Keywords); }
            }
            return new AssetShaderCollectionReport
            {
                UnityVersion = Application.unityVersion,
                GraphicsApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64).Select(x => x.ToString()).ToArray(),
                PackageVersions = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(x => x.name.StartsWith("com.unity.render-pipelines.", StringComparison.Ordinal) || x.name == "com.unity.shadergraph")
                    .OrderBy(x => x.name).Select(x => x.name + "@" + x.version).ToArray(),
                PipelineFingerprints = fingerprints.ToArray(), Candidates = candidates.ToArray()
            };
        }

        public static ShaderVariantCollection Generate(AssetShaderSettings settings, string assetPath)
        {
            if (settings == null) { throw new ArgumentNullException(nameof(settings)); }
            AssetShaderBuildSession.Validate(settings);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".shadervariants", StringComparison.Ordinal))
            { throw new ArgumentException("Choose a new Assets/*.shadervariants path.", nameof(assetPath)); }
            if (File.Exists(assetPath)) { throw new IOException("Variant collection already exists: " + assetPath); }
            var collection = new ShaderVariantCollection();
            try
            {
                foreach (var variant in settings.RequiredVariants)
                { collection.Add(new ShaderVariantCollection.ShaderVariant(variant.Shader, variant.PassType, variant.Keywords)); }
                AssetDatabase.CreateAsset(collection, assetPath);
                AssetDatabase.SaveAssetIfDirty(collection);
                return collection;
            }
            catch { UnityEngine.Object.DestroyImmediate(collection); throw; }
        }

        public static void BatchCollect()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-assetGroups");
            if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException("Required: -assetGroups"); }
            var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>(args[index + 1]);
            Debug.Log(JsonUtility.ToJson(Collect(groups), true));
        }
    }
}
