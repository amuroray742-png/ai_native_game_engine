using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    public static class AssetBuilder
    {
        /// <summary>Builds to a fresh directory. Never overwrites a published build; failed staging is retained for diagnosis.</summary>
        public static AssetCatalog Build(AssetGroups settings, string destination)
        {
            destination = Path.GetFullPath(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) { throw new IOException("Output already exists: " + destination); }
            var plan = AssetBuildPlan.Create(settings);
            // Keep intermediate bundles outside Assets so Editor refresh cannot import partial products.
            string staging = Path.GetFullPath("BuildArtifacts/Assets/" + Guid.NewGuid().ToString("N"));
            if (!string.Equals(Path.GetPathRoot(staging), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            { throw new IOException("Atomic publication requires output on the project volume."); }
            Directory.CreateDirectory(staging);
            var manifest = BuildPipeline.BuildAssetBundles(staging, plan.Definitions,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
                BuildTarget.StandaloneWindows64);
            if (manifest == null) { throw new AssetException("BuildFailed", "AssetBundle build failed; retained " + staging); }
            var report = BuildReport.GetLatestReport();
            if (report == null || report.summary.result != BuildResult.Succeeded) { throw new AssetException("MissingBuildReport", "A successful current BuildReport is required."); }
            File.Copy("Library/LastBuild.buildreport", Path.Combine(staging, "assets.buildreport"));
            ValidateReport(plan, report, staging);
            foreach (var definition in plan.Definitions.Where(x => x.assetNames.Any(p => p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))))
            {
                var bundle = AssetBundle.LoadFromFile(Path.Combine(staging, definition.assetBundleName));
                if (bundle == null) { throw new AssetException("SceneBundleInvalid", definition.assetBundleName); }
                try
                {
                    if (!new HashSet<string>(bundle.GetAllScenePaths(), StringComparer.OrdinalIgnoreCase).SetEquals(definition.assetNames))
                    { throw new AssetException("SceneEvidenceMismatch", definition.assetBundleName); }
                }
                finally { bundle.Unload(true); }
            }
            var catalog = new AssetCatalog { UnityVersion = Application.unityVersion, Assets = plan.Addresses };
            catalog.Bundles = manifest.GetAllAssetBundles().OrderBy(x => x, StringComparer.Ordinal).Select(name =>
            {
                string path = Path.Combine(staging, name);
                if (!BuildPipeline.GetCRCForAssetBundle(path, out uint crc)) { throw new AssetException("CrcUnavailable", name); }
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                return new BundleEntry { Name = name, Dependencies = manifest.GetDirectDependencies(name).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    Size = stream.Length, Crc = crc, Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
            }).ToArray();
            catalog.Validate();
            if (!new HashSet<string>(catalog.Bundles.Select(x => x.Name)).SetEquals(plan.Definitions.Select(x => x.assetBundleName)))
            { throw new AssetException("UnexpectedBundles", "Manifest differs from build definitions."); }
            foreach (var bundle in catalog.Bundles)
            {
                if (!plan.Dependencies[bundle.Name].SetEquals(bundle.Dependencies))
                { throw new AssetException("DependencyEvidenceMismatch", "Actual dependencies differ from plan: " + bundle.Name); }
            }
            File.WriteAllText(Path.Combine(staging, "catalog.json"), JsonUtility.ToJson(catalog, true));
            File.WriteAllText(Path.Combine(staging, "root-manifest.txt"), Path.GetFileName(staging) + ".manifest");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(staging, destination);
            return catalog;
        }
        internal static void ValidateReport(AssetBuildPlan plan, BuildReport report, string output)
        {
            if (report == null) { throw new AssetException("MissingBuildReport", "Ownership verification requires a build report."); }
            var files = report.GetFiles();
            var actual = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var rows = new List<string> { "guid\tasset\tbundle\tpackedFile" };
            var exclusions = new List<string>(plan.Excluded);
            foreach (var packed in report.packedAssets)
            {
                var matches = files.Where(x => Path.GetFileName(x.path) == packed.shortPath).ToArray();
                if (matches.Length != 1) { throw new AssetException("ReportMappingIncomplete", "Cannot map packed file: " + packed.shortPath); }
                string owner = Path.GetFileName(Path.GetDirectoryName(matches[0].path));
                foreach (var item in packed.contents)
                {
                    string path = item.sourceAssetPath;
                    if (!AssetBuildPlan.IsPackable(path))
                    {
                        bool generated = path == "AssetBundle Object" || path.StartsWith("Built-in AssetBundleManifest:", StringComparison.Ordinal);
                        bool sceneMetadata = string.IsNullOrEmpty(path) && item.sourceAssetGUID == default(GUID)
                            && (item.type == typeof(AssetBundle) || item.type?.FullName == "UnityEngine.PreloadData");
                        bool builtin = path == "Resources/unity_builtin_extra" || path == "Library/unity default resources";
                        bool script = AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(MonoScript) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                        if (!generated && !sceneMetadata && !builtin && !script) { throw new AssetException("UnknownReportSource", "Unclassified packed source: " + path); }
                        exclusions.Add(sceneMetadata ? "generated-scene-metadata:" + item.type.FullName : path);
                        continue;
                    }
                    if (!actual.TryGetValue(path, out var owners)) { owners = new HashSet<string>(); actual.Add(path, owners); }
                    if (item.sourceAssetGUID.ToString() != AssetDatabase.AssetPathToGUID(path))
                    { throw new AssetException("SourceGuidMismatch", "Packed GUID differs from the source: " + path); }
                    owners.Add(owner);
                    rows.Add(item.sourceAssetGUID + "\t" + path + "\t" + owner + "\t" + packed.shortPath);
                }
            }
            File.WriteAllLines(Path.Combine(output, "actual-ownership.tsv"), rows);
            File.WriteAllLines(Path.Combine(output, "planned-ownership.tsv"), plan.Owners.Select(x => AssetDatabase.AssetPathToGUID(x.Key) + "\t" + x.Key + "\t" + x.Value));
            File.WriteAllLines(Path.Combine(output, "excluded.txt"), exclusions.Distinct().OrderBy(x => x, StringComparer.Ordinal));
            var errors = new List<string>();
            foreach (var pair in actual)
            {
                if (!plan.Owners.TryGetValue(pair.Key, out string expected) || pair.Value.Count != 1 || !pair.Value.Contains(expected))
                { errors.Add(pair.Key + " -> " + string.Join(",", pair.Value)); }
            }
            foreach (var pair in plan.Owners)
            {
                if (!pair.Key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && !actual.ContainsKey(pair.Key)) { errors.Add("Missing evidence: " + pair.Key); }
            }
            File.WriteAllLines(Path.Combine(output, "ownership-errors.txt"), errors);
            if (errors.Count != 0) { throw new AssetException("OwnershipVerificationFailed", string.Join("\n", errors)); }
        }
        public static void BatchBuild()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Read(string key)
            {
                int index = Array.IndexOf(args, key);
                if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException("Missing argument " + key); }
                return args[index + 1];
            }
            Build(AssetDatabase.LoadAssetAtPath<AssetGroups>(Read("-assetGroups")), Read("-assetOutput"));
        }
    }
}
