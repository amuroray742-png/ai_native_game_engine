using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace AiNative.UnityCore.Assets.Editor
{
    internal static class ResourceCollection
    {
        internal static ResourceAddress[] Collect(ResourceGroup group)
        {
            if (!Enum.IsDefined(typeof(ResourceAddressMode), group.AddressMode))
            {
                throw new AssetException("InvalidAddressMode", "Unknown address mode: " + group.Id);
            }
            var entries = new SortedDictionary<string, ResourceAddress>(StringComparer.Ordinal);
            foreach (var item in group.Assets)
            {
                string path = AssetDatabase.GetAssetPath(item.Asset);
                if (!entries.TryAdd(path, new ResourceAddress { Asset = item.Asset, Address = Address(group, path, item.Address) }))
                {
                    throw new AssetException("InvalidEntry", "Repeated explicit resource: " + path);
                }
            }
            foreach (var folder in group.Folders)
            {
                string path = AssetDatabase.GetAssetPath(folder);
                if (!AssetDatabase.IsValidFolder(path)) { throw new AssetException("InvalidFolder", "Expected a project folder in group " + group.Id); }
                if (group.AddressMode == ResourceAddressMode.Manual)
                {
                    throw new AssetException("FolderAddressMode", "Folder collection requires FullPath or GroupAndAssetName in group " + group.Id);
                }
                foreach (string assetPath in AssetDatabase.FindAssets("", new[] { path }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(x => x, StringComparer.Ordinal))
                {
                    // Explicit entries override folder-generated addresses; overlapping folders collect once.
                    if (!AssetBuildPlan.IsPackable(assetPath) || entries.ContainsKey(assetPath)) { continue; }
                    entries.Add(assetPath, new ResourceAddress { Asset = AssetDatabase.LoadMainAssetAtPath(assetPath), Address = Address(group, assetPath, "") });
                }
            }
            return entries.Values.ToArray();
        }

        private static string Address(ResourceGroup group, string path, string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) || group.AddressMode == ResourceAddressMode.Manual) { return configured; }
            string extension = Path.GetExtension(path);
            if (group.AddressMode == ResourceAddressMode.FullPath) { return path.Substring(0, path.Length - extension.Length); }
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
