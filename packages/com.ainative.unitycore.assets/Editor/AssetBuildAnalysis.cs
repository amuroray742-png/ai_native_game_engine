using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [Serializable]
    public sealed class AssetSourceAnalysis
    {
        public string Guid;
        public string Path;
        public string Address;
        public string Bundle;
        public string DependencyHash;
        public string[] Dependencies;
        public long SourceFileBytes;
        public long DependencyFileBytes;
    }

    [Serializable]
    public sealed class AssetBuildAnalysis
    {
        public int FormatVersion = 1;
        public int ContentVersion;
        public string Platform;
        public string UnityVersion;
        public string Backend;
        public long TotalFileBytes;
        public BundleEntry[] Bundles;
        public AssetSourceAnalysis[] Sources;
        public string[] CollectionDecisions;
        public string[] Warnings;
        public AssetShaderCollectionReport RenderingContext;

        internal static void Write(AssetGroups settings, AssetBuildPlan plan, AssetCatalog catalog, int version, string output)
        {
            var bundles = catalog.Bundles.ToDictionary(x => x.Name);
            long Cost(string bundle)
            {
                var visited = new HashSet<string>();
                void Visit(string name)
                {
                    if (!visited.Add(name)) { return; }
                    foreach (string child in bundles[name].Dependencies) { Visit(child); }
                }
                Visit(bundle);
                return visited.Sum(name => bundles[name].Size);
            }
            var addresses = catalog.Assets.ToDictionary(x => x.Path);
            var warnings = new List<string>();
            long total = catalog.Bundles.Sum(x => x.Size);
            if (settings.TotalWarningBytes > 0 && total > settings.TotalWarningBytes) { warnings.Add("Total bundle file bytes exceed threshold: " + total); }
            foreach (var bundle in catalog.Bundles)
            {
                if (settings.BundleWarningBytes > 0 && bundle.Size > settings.BundleWarningBytes)
                { warnings.Add(bundle.Name + " exceeds file byte threshold: " + bundle.Size); }
            }
            var decisions = new List<string>(plan.CollectionDecisions);
            foreach (string path in plan.Excluded.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                string reason = addresses.ContainsKey(path) ? "public-in-another-group" : plan.Owners.ContainsKey(path) ? "reincluded-as-dependency" : "not-collected";
                decisions.Add(path + "\t" + reason);
            }
            var report = new AssetBuildAnalysis
            {
                ContentVersion = version, Platform = catalog.Platform, UnityVersion = catalog.UnityVersion,
                Backend = settings.BuildBackend.ToString(), TotalFileBytes = total, Bundles = catalog.Bundles,
                Sources = plan.Owners.Select(x => new AssetSourceAnalysis
                {
                    Guid = AssetDatabase.AssetPathToGUID(x.Key), Path = x.Key, Bundle = x.Value,
                    DependencyHash = AssetDatabase.GetAssetDependencyHash(x.Key).ToString(),
                    Dependencies = AssetDatabase.GetDependencies(x.Key, false).Where(path => path != x.Key).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                    SourceFileBytes = SourceSize(x.Key),
                    Address = addresses.TryGetValue(x.Key, out var address) ? address.Address : "",
                    DependencyFileBytes = Cost(x.Value)
                }).ToArray(),
                CollectionDecisions = decisions.ToArray(), Warnings = warnings.ToArray()
            };
            report.RenderingContext = AssetShaderCollection.Collect(settings, plan);
            File.WriteAllText(Path.Combine(output, "build-analysis.json"), JsonUtility.ToJson(report, true));
            foreach (string warning in warnings) { Debug.LogWarning(warning); }
        }

        private static long SourceSize(string path)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            string physical = package == null ? path : Path.Combine(package.resolvedPath, path.Substring(package.assetPath.Length).TrimStart('/'));
            return File.Exists(physical) ? new FileInfo(physical).Length : 0;
        }

        public static AssetBuildAnalysis Read(string path)
        {
            if (Directory.Exists(path)) { path = Path.Combine(path, "build-analysis.json"); }
            if (!File.Exists(path)) { throw new AssetException("AnalysisMissing", "Rebuild content to generate build-analysis.json: " + path); }
            var report = JsonUtility.FromJson<AssetBuildAnalysis>(File.ReadAllText(path));
            if (report == null || report.FormatVersion != 1 || report.Sources == null || report.Bundles == null)
            { throw new AssetException("InvalidAnalysis", "Unsupported or incomplete build analysis."); }
            return report;
        }

        public static string[] Compare(AssetBuildAnalysis before, AssetBuildAnalysis after)
        {
            var changes = new List<string>();
            if (before.RenderingContext != null && after.RenderingContext != null)
            {
                if (!before.RenderingContext.PipelineFingerprints.SequenceEqual(after.RenderingContext.PipelineFingerprints)) { changes.Add("rendering-pipeline-config-changed"); }
                if (!before.RenderingContext.PackageVersions.SequenceEqual(after.RenderingContext.PackageVersions)) { changes.Add("rendering-package-versions-changed"); }
                if (!before.RenderingContext.GraphicsApis.SequenceEqual(after.RenderingContext.GraphicsApis)) { changes.Add("rendering-graphics-apis-changed"); }
            }
            else if (before.RenderingContext != after.RenderingContext) { changes.Add("rendering-context-evidence-added-or-missing"); }
            var previous = before.Sources.ToDictionary(x => x.Guid);
            var current = after.Sources.ToDictionary(x => x.Guid);
            foreach (string guid in previous.Keys.Union(current.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!previous.TryGetValue(guid, out var old)) { changes.Add("added\t" + current[guid].Path); }
                else if (!current.TryGetValue(guid, out var next)) { changes.Add("removed\t" + old.Path); }
                else if (old.Path != next.Path || old.Address != next.Address || old.Bundle != next.Bundle)
                { changes.Add("changed\t" + guid + "\t" + old.Path + " -> " + next.Path + "\t" + old.Address + " -> " + next.Address + "\t" + old.Bundle + " -> " + next.Bundle); }
            }
            var oldBundles = before.Bundles.ToDictionary(x => x.Name);
            var newBundles = after.Bundles.ToDictionary(x => x.Name);
            foreach (string name in oldBundles.Keys.Union(newBundles.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!oldBundles.TryGetValue(name, out var old)) { changes.Add("bundle-added\t" + name); }
                else if (!newBundles.TryGetValue(name, out var next)) { changes.Add("bundle-removed\t" + name); }
                else if (old.Sha256 != next.Sha256) { changes.Add("bundle-changed\t" + name + "\t" + old.Size + " -> " + next.Size); }
            }
            changes.Add("total-file-bytes\t" + before.TotalFileBytes + " -> " + after.TotalFileBytes);
            return changes.ToArray();
        }

        public static void BatchCompare()
        {
            string[] args = Environment.GetCommandLineArgs();
            string ReadArgument(string name)
            {
                int index = Array.IndexOf(args, name);
                if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException("Required: " + name); }
                return args[index + 1];
            }
            Debug.Log(string.Join("\n", Compare(Read(ReadArgument("-assetBefore")), Read(ReadArgument("-assetAfter")))));
        }
    }
}
