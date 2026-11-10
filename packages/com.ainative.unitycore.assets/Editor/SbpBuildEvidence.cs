using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [Serializable]
    internal sealed class SbpBuildEvidence
    {
        public int FormatVersion = 1;
        public List<SbpBundleEvidence> Bundles = new List<SbpBundleEvidence>();
        public List<SbpFileEvidence> Files = new List<SbpFileEvidence>();

        internal static void Validate(AssetBuildPlan plan, SbpBuildEvidence evidence, string output)
        {
            if (evidence == null || evidence.FormatVersion != 1 || evidence.Files == null || evidence.Bundles == null || evidence.Files.Count == 0)
            { throw new AssetException("MissingBuildReport", "Complete SBP write evidence is required."); }
            var bundleNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bundle in evidence.Bundles)
            {
                if (!bundleNames.Add(bundle.Name) || !plan.Definitions.Any(x => x.assetBundleName == bundle.Name))
                { throw new AssetException("UnexpectedBundles", bundle.Name); }
                string path = Path.Combine(output, bundle.Name);
                if (!File.Exists(path)) { throw new AssetException("BundleEvidenceMismatch", "Missing SBP output: " + bundle.Name); }
                // GetCRCForAssetBundle depends on the native manifest, which SBP does not emit.
                // Validate SBP's archive CRC by opening the actual bundle with Unity's CRC check.
                var loaded = AssetBundle.LoadFromFile(path, bundle.Crc);
                if (loaded == null) { throw new AssetException("BundleEvidenceMismatch", "SBP archive CRC failed: " + bundle.Name); }
                loaded.Unload(true);
            }
            if (!bundleNames.SetEquals(plan.Definitions.Select(x => x.assetBundleName)))
            { throw new AssetException("UnexpectedBundles", "SBP bundle set differs from plan."); }
            var actual = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            var coveredBundles = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<string> { "guid\tasset\tbundle\tpackedFile\tlocalId" };
            var exclusions = new List<string>(plan.Excluded);
            var sceneBundles = plan.Definitions.Where(x => x.assetNames.Any(p => p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))).Select(x => x.assetBundleName).ToHashSet();
            foreach (var file in evidence.Files)
            {
                if (string.IsNullOrEmpty(file.File) || !files.Add(file.File) || !bundleNames.Contains(file.Bundle) || file.Objects == null || file.Objects.Count == 0)
                { throw new AssetException("ReportMappingIncomplete", "Invalid or empty SBP file mapping: " + file.File); }
                coveredBundles.Add(file.Bundle);
                foreach (var item in file.Objects)
                {
                    string source = item.SourcePath;
                    if (!AssetBuildPlan.IsPackable(source))
                    {
                        bool builtin = source == "Resources/unity_builtin_extra" || source == "Library/unity default resources";
                        bool script = AssetDatabase.GetMainAssetTypeAtPath(source) == typeof(MonoScript) || source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                        bool generated = item.Guid == "00000000000000000000000000000000" && item.FileType == "NonAssetType" && string.IsNullOrEmpty(source)
                            && (item.FilePath == "temp:/AssetBundle" || item.FilePath == "temp:/PreloadData"
                                || (sceneBundles.Contains(file.Bundle) && item.FilePath.StartsWith("temp:/", StringComparison.Ordinal)));
                        if (!builtin && !script && !generated) { throw new AssetException("UnknownReportSource", source + " / " + item.Guid + " / " + item.FilePath); }
                        exclusions.Add(file.Bundle + ":" + (generated ? "generated:" + item.FilePath : source));
                        continue;
                    }
                    if (AssetDatabase.AssetPathToGUID(source) != item.Guid)
                    { throw new AssetException("SourceGuidMismatch", source); }
                    if (!actual.TryGetValue(source, out var owners)) { owners = new HashSet<string>(StringComparer.Ordinal); actual.Add(source, owners); }
                    owners.Add(file.Bundle);
                    rows.Add(item.Guid + "\t" + source + "\t" + file.Bundle + "\t" + file.File + "\t" + item.LocalId);
                }
            }
            if (!bundleNames.SetEquals(coveredBundles)) { throw new AssetException("ReportMappingIncomplete", "Some bundles have no write evidence."); }
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
    }
    [Serializable]
    internal sealed class SbpBundleEvidence
    {
        public string Name;
        public uint Crc;
        public string[] Dependencies;
    }
    [Serializable]
    internal sealed class SbpFileEvidence
    {
        public string File;
        public string Bundle;
        public List<SbpObjectEvidence> Objects = new List<SbpObjectEvidence>();
    }
    [Serializable]
    internal sealed class SbpObjectEvidence
    {
        public string Guid;
        public long LocalId;
        public string FileType;
        public string FilePath;
        public string SourcePath;
    }
}
