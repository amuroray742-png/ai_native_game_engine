#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    [Serializable]
    public sealed class AssetCatalog
    {
        public int FormatVersion = 1;
        public string UnityVersion = "";
        public string Platform = "StandaloneWindows64";
        public AssetEntry[] Assets = Array.Empty<AssetEntry>();
        public BundleEntry[] Bundles = Array.Empty<BundleEntry>();

        public void Validate()
        {
            if (FormatVersion != 1 || UnityVersion != Application.unityVersion || Platform != "StandaloneWindows64")
            {
                throw new AssetException("CatalogIncompatible", "Catalog version, Unity version or platform differs.");
            }
            var bundles = new Dictionary<string, BundleEntry>(StringComparer.Ordinal);
            if (Bundles == null || Assets == null) { throw new AssetException("InvalidCatalog", "Catalog arrays are required."); }
            foreach (var bundle in Bundles)
            {
                if (bundle == null || bundle.Dependencies == null || string.IsNullOrWhiteSpace(bundle.Name) || bundle.Name != Path.GetFileName(bundle.Name)
                    || bundle.Name.Contains("..") || bundle.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                    || bundle.Size < 0 || !bundles.TryAdd(bundle.Name, bundle))
                {
                    throw new AssetException("InvalidCatalog", "Invalid or duplicate bundle name.");
                }
            }
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in Assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Address) || !addresses.Add(asset.Address)
                    || string.IsNullOrEmpty(asset.Bundle) || !bundles.ContainsKey(asset.Bundle) || string.IsNullOrWhiteSpace(asset.Path))
                {
                    throw new AssetException("InvalidCatalog", "Invalid address or bundle reference.", asset?.Address ?? "");
                }
            }
            var complete = new HashSet<string>();
            var visiting = new HashSet<string>();
            foreach (string name in bundles.Keys) { Visit(name); }
            void Visit(string name)
            {
                if (complete.Contains(name)) { return; }
                if (string.IsNullOrEmpty(name) || !bundles.ContainsKey(name) || !visiting.Add(name))
                {
                    throw new AssetException("InvalidDependencies", "Missing or cyclic bundle dependency: " + name);
                }
                foreach (string dependency in bundles[name].Dependencies) { Visit(dependency); }
                visiting.Remove(name);
                complete.Add(name);
            }
        }
    }

    [Serializable]
    public sealed class AssetEntry
    {
        public string Address = "";
        public string Path = "";
        public string Bundle = "";
        public bool IsScene;
    }

    [Serializable]
    public sealed class BundleEntry
    {
        public string Name = "";
        public string[] Dependencies = Array.Empty<string>();
        public long Size;
        public uint Crc;
        public string Sha256 = "";
    }
}
