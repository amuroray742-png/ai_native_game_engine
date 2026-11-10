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
            => Build(settings, destination, new AssetBuildOptions());

        public static AssetCatalog Build(AssetGroups settings, string destination, AssetBuildOptions options)
        {
            if (settings == null) { throw new ArgumentNullException(nameof(settings)); }
            // Native and SBP builds can unload objects while ignoring managed references.
            bool transient = !EditorUtility.IsPersistent(settings);
            HideFlags previous = settings.hideFlags;
            if (transient) { settings.hideFlags |= HideFlags.HideAndDontSave; }
            try { return BuildContent(settings, destination, options); }
            finally { if (transient && settings != null) { settings.hideFlags = previous; } }
        }

        private static AssetCatalog BuildContent(AssetGroups settings, string destination, AssetBuildOptions options)
        {
            if (options == null) { throw new ArgumentNullException(nameof(options)); }
            destination = Path.GetFullPath(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) { throw new IOException("Output already exists: " + destination); }
            var plan = AssetBuildPlan.Create(settings);
            // Keep intermediate bundles outside Assets so Editor refresh cannot import partial products.
            string staging = Path.GetFullPath("BuildArtifacts/Assets/" + Guid.NewGuid().ToString("N"));
            if (!string.Equals(Path.GetPathRoot(staging), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            { throw new IOException("Atomic publication requires output on the project volume."); }
            Directory.CreateDirectory(staging);
            var metadata = new AssetBuildMetadata { Backend = settings.BuildBackend, UseCache = settings.BuildBackend == AssetBuildBackend.ScriptableBuildPipeline && options.UseCache };
            Dictionary<string, uint> crcs;
            var actualDependencies = settings.BuildBackend == AssetBuildBackend.Builtin
                ? BuildBuiltin(plan, staging, metadata, out crcs)
                : SbpAssetBuild.Build(plan, staging, options, metadata, out crcs);
            var dependencies = BundleDependencyGraph.VerifyAndReduce(plan, actualDependencies);
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
            catalog.Bundles = dependencies.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(name =>
            {
                string path = Path.Combine(staging, name);
                uint crc = crcs[name];
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                return new BundleEntry { Name = name, Dependencies = dependencies[name].OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    Size = stream.Length, Crc = crc, Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
            }).ToArray();
            catalog.Validate();
            if (!new HashSet<string>(catalog.Bundles.Select(x => x.Name)).SetEquals(plan.Definitions.Select(x => x.assetBundleName)))
            { throw new AssetException("UnexpectedBundles", "Manifest differs from build definitions."); }
            File.WriteAllText(Path.Combine(staging, "catalog.json"), JsonUtility.ToJson(catalog, true));
            metadata.PreservationSha256 = FileHash(Path.Combine(staging, metadata.PreservationFile));
            File.WriteAllText(Path.Combine(staging, "build-metadata.json"), JsonUtility.ToJson(metadata, true));
            Publish(staging, destination);
            return catalog;
        }
        internal static void Publish(string staging, string destination)
        {
            // Keep publication independent of handles held during validation. These copies have
            // never been opened by Unity, and the final rename remains atomic on Windows.
            string publication = staging + "-publish";
            Directory.CreateDirectory(publication);
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(publication, Path.GetRelativePath(staging, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(publication, destination); return; }
                catch (IOException) when (Application.platform == RuntimePlatform.WindowsEditor && attempt < 2
                    && Directory.Exists(publication) && !Directory.Exists(destination) && !File.Exists(destination))
                {
                    // Reproduced without Unity: a fresh directory rename can receive transient
                    // Windows access denial, then succeed after 100 ms. Never retry an existing target.
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        private static Dictionary<string, HashSet<string>> BuildBuiltin(AssetBuildPlan plan, string staging, AssetBuildMetadata metadata, out Dictionary<string, uint> crcs)
        {
            var manifest = BuildPipeline.BuildAssetBundles(staging, plan.Definitions,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
                BuildTarget.StandaloneWindows64);
            if (manifest == null) { throw new AssetException("BuildFailed", "AssetBundle build failed; retained " + staging); }
            var report = BuildReport.GetLatestReport();
            if (report == null || report.summary.result != BuildResult.Succeeded) { throw new AssetException("MissingBuildReport", "A successful current BuildReport is required."); }
            File.Copy("Library/LastBuild.buildreport", Path.Combine(staging, "assets.buildreport"));
            ValidateReport(plan, report, staging);
            metadata.PreservationFile = Path.GetFileName(staging) + ".manifest";
            File.WriteAllText(Path.Combine(staging, "root-manifest.txt"), metadata.PreservationFile);
            crcs = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (string name in manifest.GetAllAssetBundles())
            {
                if (!BuildPipeline.GetCRCForAssetBundle(Path.Combine(staging, name), out uint crc)) { throw new AssetException("CrcUnavailable", name); }
                crcs.Add(name, crc);
            }
            return manifest.GetAllAssetBundles().ToDictionary(x => x, x => new HashSet<string>(manifest.GetDirectDependencies(x), StringComparer.Ordinal), StringComparer.Ordinal);
        }

        internal static string FileHash(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
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
            Build(AssetDatabase.LoadAssetAtPath<AssetGroups>(Read("-assetGroups")), Read("-assetOutput"),
                new AssetBuildOptions { UseCache = !args.Contains("-assetCleanBuild") });
        }
    }
}
