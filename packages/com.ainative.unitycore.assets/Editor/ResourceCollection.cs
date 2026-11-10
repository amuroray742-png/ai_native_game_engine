using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace AiNative.UnityCore.Assets.Editor
{
    internal static class ResourceCollection
    {
        internal static (UnityEngine.Object Asset, string Address)[] Collect(ResourceGroup group, ResourceAddressMode mode)
        {
            if (!Enum.IsDefined(typeof(ResourceAddressMode), mode))
            {
                throw new AssetException("InvalidAddressMode", "Unknown address mode: " + group.Id);
            }
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
                    if (!AssetBuildPlan.IsPackable(assetPath) || entries.ContainsKey(assetPath)) { continue; }
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
