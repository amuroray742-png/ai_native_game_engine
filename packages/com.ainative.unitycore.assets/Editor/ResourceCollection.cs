using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace AiNative.UnityCore.Assets.Editor
{
    internal static class ResourceCollection
    {
        internal static (UnityEngine.Object Asset, string Address)[] Collect(ResourceGroup group, ResourceAddressMode mode, List<string> excluded,
            BuildTarget target = BuildTarget.StandaloneWindows64, List<string> decisions = null)
        {
            var overrides = group.PlatformFilters.Where(x => x.Target == target).ToArray();
            if (overrides.Length > 1) { throw new AssetException("InvalidFilter", "Duplicate platform filter: " + group.Id + " / " + target); }
            var platform = overrides.SingleOrDefault();
            var filter = new ResourceFilter
            {
                ExcludedFolders = platform != null && platform.OverrideFolders ? platform.Filter.ExcludedFolders : group.Filter.ExcludedFolders,
                IncludedTypes = platform != null && platform.OverrideTypes ? platform.Filter.IncludedTypes : group.Filter.IncludedTypes
            };
            var entries = new SortedDictionary<string, (UnityEngine.Object Asset, string Address)>(StringComparer.Ordinal);
            foreach (var item in group.Assets)
            {
                string path = AssetDatabase.GetAssetPath(item.Asset);
                if (!entries.TryAdd(path, (item.Asset, Address(group, path, mode))))
                {
                    throw new AssetException("InvalidEntry", "Repeated explicit resource: " + path);
                }
            }
            foreach (var folder in group.Folders)
            {
                string path = AssetDatabase.GetAssetPath(folder);
                if (!AssetDatabase.IsValidFolder(path)) { throw new AssetException("InvalidFolder", "Expected a project folder in group " + group.Id); }
                foreach (string assetPath in AssetDatabase.FindAssets("", new[] { path }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(x => x, StringComparer.Ordinal))
                {
                    // Explicit entries and overlapping folders collect each source only once.
                    if (!AssetBuildPlan.IsPackable(assetPath))
                    {
                        if (!AssetDatabase.IsValidFolder(assetPath)) { excluded.Add(assetPath); }
                        continue;
                    }
                    if (entries.ContainsKey(assetPath)) { continue; }
                    string reason = filter.Exclusion(assetPath);
                    if (reason != null)
                    {
                        excluded.Add(assetPath);
                        decisions?.Add(group.Id + "\t" + assetPath + "\t" + reason);
                        continue;
                    }
                    entries.Add(assetPath, (AssetDatabase.LoadMainAssetAtPath(assetPath), Address(group, assetPath, mode)));
                }
            }
            return entries.Values.ToArray();
        }

        private static string Address(ResourceGroup group, string path, ResourceAddressMode mode)
        {
            string extension = Path.GetExtension(path);
            if (mode == ResourceAddressMode.FullPath) { return path.Substring(0, path.Length - extension.Length); }
            return group.EffectiveName + "/" + Path.GetFileNameWithoutExtension(path);
        }

        internal static void ValidateBundleLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
            {
                throw new AssetException("InvalidBundleName", "Readable group names may contain letters, digits, '_' and '-' only: " + label);
            }
        }
    }
}
