using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.UnityCore.Assets.Editor
{
    internal sealed class AssetShaderBuildSession : IDisposable
    {
        [Serializable] private sealed class PassReport
        {
            public string ShaderGuid; public string ShaderPath; public string PassName; public string PassType;
            public int Observed; public int Retained; public int Removed; public int[] RuleHits;
        }
        [Serializable] private sealed class BuildReport
        {
            public int FormatVersion = 1;
            public string CachePolicy = "Disabled for custom shader processing";
            public AssetShaderCollectionReport Collection;
            public PassReport[] Passes;
            public string[] RequiredVariantObservations;
        }
        internal static AssetShaderBuildSession Active { get; private set; }
        private readonly AssetShaderSettings _settings;
        private readonly AssetShaderCollectionReport _collection;
        private readonly List<PassReport> _passes = new List<PassReport>();
        private readonly HashSet<int> _observedRequired = new HashSet<int>();

        internal AssetShaderBuildSession(AssetGroups groups, AssetBuildPlan plan)
        {
            if (Active != null) { throw new InvalidOperationException("Shader processing cannot be nested."); }
            Validate(groups.Shaders);
            _collection = AssetShaderCollection.Collect(groups, plan);
            _settings = UnityEngine.Object.Instantiate(groups.Shaders);
            _settings.hideFlags = HideFlags.HideAndDontSave;
            Active = this;
        }
        internal static bool Matches(AssetShaderExclusion rule, Shader shader, PassType passType, string passName, IEnumerable<string> keywords)
        {
            var set = new HashSet<string>(keywords, StringComparer.Ordinal);
            return rule.Shader == shader && (!rule.MatchPassType || rule.PassType == passType) &&
                (string.IsNullOrEmpty(rule.PassName) || rule.PassName == passName) &&
                rule.RequiredKeywords.All(set.Contains) && !rule.ForbiddenKeywords.Any(set.Contains);
        }
        internal static void Validate(AssetShaderSettings settings)
        {
            foreach (var variant in settings.RequiredVariants)
            {
                if (variant.Shader == null) { throw new AssetException("InvalidShaderVariant", "Required variant has no shader."); }
                foreach (var rule in settings.Exclusions)
                {
                    // An unspecified required pass name could match any excluded pass of this type.
                    string pass = string.IsNullOrEmpty(variant.PassName) ? rule.PassName : variant.PassName;
                    if (Matches(rule, variant.Shader, variant.PassType, pass, variant.Keywords))
                    { throw new AssetException("RequiredShaderExcluded", "Required variant matches an exclusion: " + variant.Shader.name); }
                }
            }
            if (settings.Exclusions.Any(x => x.Shader == null)) { throw new AssetException("InvalidShaderRule", "Exclusions must specify a shader."); }
        }
        internal void Process(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            var report = new PassReport { ShaderGuid = AssetDatabase.AssetPathToGUID(path), ShaderPath = path,
                PassName = snippet.passName, PassType = snippet.passType.ToString(), Observed = data.Count,
                RuleHits = new int[_settings.Exclusions.Length] };
            for (int i = data.Count - 1; i >= 0; i--)
            {
                var keywords = data[i].shaderKeywordSet.GetShaderKeywords().Select(x => x.name).ToArray();
                for (int required = 0; required < _settings.RequiredVariants.Length; required++)
                {
                    var variant = _settings.RequiredVariants[required];
                    if (variant.Shader == shader && variant.PassType == snippet.passType &&
                        (string.IsNullOrEmpty(variant.PassName) || variant.PassName == snippet.passName) &&
                        new HashSet<string>(variant.Keywords).SetEquals(keywords)) { _observedRequired.Add(required); }
                }
                for (int rule = 0; rule < _settings.Exclusions.Length; rule++)
                {
                    if (!Matches(_settings.Exclusions[rule], shader, snippet.passType, snippet.passName, keywords)) { continue; }
                    data.RemoveAt(i); report.Removed++; report.RuleHits[rule]++; break;
                }
            }
            report.Retained = data.Count;
            _passes.Add(report);
        }
        internal void Write(string directory)
        {
            var report = new BuildReport
            {
                Collection = _collection, Passes = _passes.ToArray(),
                RequiredVariantObservations = _settings.RequiredVariants.Select((x, i) =>
                    x.Shader.name + "/" + x.PassType + "/" + x.PassName + ": " +
                    (_observedRequired.Contains(i) ? "observed by this callback; verify in Player" : "not observed; retention unproven")).ToArray()
            };
            File.WriteAllText(Path.Combine(directory, "shader-build-report.json"), JsonUtility.ToJson(report, true));
        }
        public void Dispose()
        {
            if (ReferenceEquals(Active, this)) { Active = null; }
            UnityEngine.Object.DestroyImmediate(_settings);
        }
    }

    internal sealed class AssetShaderPreprocessor : IPreprocessShaders
    {
        public int callbackOrder => 0;
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        { AssetShaderBuildSession.Active?.Process(shader, snippet, data); }
    }
}
