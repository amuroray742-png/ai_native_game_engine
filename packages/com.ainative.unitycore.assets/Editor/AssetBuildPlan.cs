using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace AiNative.UnityCore.Assets.Editor
{
    public sealed class AssetBuildPlan
    {
        internal readonly SortedDictionary<string, string> Owners = new SortedDictionary<string, string>(StringComparer.Ordinal);
        internal readonly List<string> Excluded = new List<string>();
        internal AssetEntry[] Addresses;
        internal Dictionary<string, HashSet<string>> Dependencies;
        public AssetBundleBuild[] Definitions { get; private set; }
        public static AssetBuildPlan Create(AssetGroups settings)
        {
            if (settings == null) { throw new ArgumentNullException(nameof(settings)); }
            if (!Enum.IsDefined(typeof(AssetBuildBackend), settings.BuildBackend)) { throw new AssetException("InvalidBuildBackend", "Unknown build backend."); }
            if (!Enum.IsDefined(typeof(BundleNamingMode), settings.BundleNaming)) { throw new AssetException("InvalidNamingMode", "Unknown bundle naming mode."); }
            if (!Enum.IsDefined(typeof(ResourceAddressMode), settings.AddressMode)) { throw new AssetException("InvalidAddressMode", "Unknown global address mode."); }
            var result = new AssetBuildPlan();
            var groups = new Dictionary<string, ResourceGroup>(StringComparer.Ordinal);
            var explicitOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            var references = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var addresses = new List<AssetEntry>();
            var usedAddresses = new Dictionary<string, string>(StringComparer.Ordinal);
            var sceneGroups = new HashSet<string>();
            foreach (var group in settings.Groups.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(group.Id) || !groups.TryAdd(group.Id, group)) { throw new AssetException("InvalidGroup", "Empty or duplicate group ID."); }
                bool? scene = null;
                foreach (var item in ResourceCollection.Collect(group, settings.AddressMode, result.Excluded))
                {
                    string path = AssetDatabase.GetAssetPath(item.Asset);
                    if (string.IsNullOrWhiteSpace(item.Address) || !IsPackable(path) || AssetDatabase.IsSubAsset(item.Asset))
                    { throw new AssetException("InvalidEntry", "Missing or unsupported main asset in group " + group.Id + ": " + path, item.Address); }
                    if (explicitOwners.TryGetValue(path, out string previousGroup))
                    { throw new AssetException("InvalidEntry", "Resource " + path + " is collected by both groups " + previousGroup + " and " + group.Id, item.Address); }
                    string source = "group " + group.Id + ": " + path;
                    if (usedAddresses.TryGetValue(item.Address, out string previousSource))
                    { throw new AssetException("InvalidEntry", "Address collision between " + previousSource + " and " + source, item.Address); }
                    explicitOwners.Add(path, group.Id);
                    usedAddresses.Add(item.Address, source);
                    bool isScene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                    if (scene.HasValue && scene != isScene) { throw new AssetException("MixedSceneGroup", "Scene and ordinary assets must use separate groups."); }
                    scene = isScene;
                    addresses.Add(new AssetEntry { Address = item.Address, Path = path, IsScene = isScene });
                    foreach (string dependency in AssetDatabase.GetDependencies(path, true))
                    {
                        if (!IsPackable(dependency)) { result.Excluded.Add(dependency); continue; }
                        if (!references.TryGetValue(dependency, out var users)) { users = new SortedSet<string>(StringComparer.Ordinal); references.Add(dependency, users); }
                        users.Add(group.Id);
                    }
                }
                if (scene == true) { sceneGroups.Add(group.Id); }
            }
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string BundleName(string prefix, IEnumerable<string> ids)
            {
                string[] sortedIds = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                string identity = prefix + string.Join("", sortedIds.Select(x => x.Length + ":" + x));
                using var sha = SHA256.Create();
                // Preserve the existing hash input for previously published configurations.
                string hashInput = string.Join("", sortedIds.Select(x => x.Length + ":" + x));
                string name = prefix + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(hashInput))).Replace("-", "").ToLowerInvariant() + ".bundle";
                if (settings.BundleNaming == BundleNamingMode.GroupName)
                {
                    string[] labels = sortedIds.Select(x => groups[x].EffectiveName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    foreach (string label in labels) { ResourceCollection.ValidateBundleLabel(label); }
                    // Unity normalizes physical AssetBundle names to lowercase.
                    name = ((prefix == "shared_" ? "share_" : prefix) + string.Join("_", labels) + ".bundle").ToLowerInvariant();
                }
                if (names.TryGetValue(name, out string previous) && previous != identity)
                { throw new AssetException("BundleNameCollision", name + " maps to different group sets: " + previous + " and " + identity); }
                names[name] = identity;
                return name;
            }
            foreach (var pair in references.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                string owner;
                if (explicitOwners.TryGetValue(pair.Key, out string explicitGroup)) { owner = BundleName("group_", new[] { explicitGroup }); }
                else if (pair.Value.Count == 1 && !sceneGroups.Contains(pair.Value.First())) { owner = BundleName("group_", pair.Value); }
                else { owner = BundleName("shared_", pair.Value); }
                result.Owners.Add(pair.Key, owner);
            }
            foreach (var entry in addresses) { entry.Bundle = result.Owners[entry.Path]; }
            foreach (string path in result.Owners.Keys)
            {
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) { continue; }
                var duplicate = AssetDatabase.LoadAllAssetsAtPath(path).Where(x => x != null)
                    .GroupBy(x => (x.GetType(), x.name)).FirstOrDefault(x => x.Count() > 1);
                if (duplicate != null) { throw new AssetException("SubAssetAmbiguous", "Duplicate subasset type/name: " + path + " / " + duplicate.Key); }
            }
            result.Addresses = addresses.OrderBy(x => x.Address, StringComparer.Ordinal).ToArray();
            result.Definitions = result.Owners.GroupBy(x => x.Value).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(g => new AssetBundleBuild { assetBundleName = g.Key, assetNames = g.Select(x => x.Key).ToArray() }).ToArray();
            var graph = result.Definitions.ToDictionary(x => x.assetBundleName, x => new HashSet<string>());
            foreach (var pair in result.Owners)
            {
                foreach (string dependency in AssetDatabase.GetDependencies(pair.Key, false))
                {
                    if (result.Owners.TryGetValue(dependency, out string owner) && owner != pair.Value) { graph[pair.Value].Add(owner); }
                }
            }
            var done = new HashSet<string>();
            var chain = new List<string>();
            foreach (string bundle in graph.Keys) { Visit(bundle); }
            result.Dependencies = graph;
            void Visit(string bundle)
            {
                if (done.Contains(bundle)) { return; }
                if (chain.Contains(bundle)) { throw new AssetException("CyclicBundles", string.Join(" -> ", chain.Concat(new[] { bundle }))); }
                chain.Add(bundle);
                foreach (string child in graph[bundle].OrderBy(x => x, StringComparer.Ordinal)) { Visit(child); }
                chain.RemoveAt(chain.Count - 1);
                done.Add(bundle);
            }
            return result;
        }
        internal static bool IsPackable(string path)
        {
            if (!(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) || AssetDatabase.IsValidFolder(path)) { return false; }
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type != null && type != typeof(DefaultAsset) && type != typeof(MonoScript) && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);
        }
    }
}
