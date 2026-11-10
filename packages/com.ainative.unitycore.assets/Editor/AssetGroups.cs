using System;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [CreateAssetMenu(menuName = "AI Native/Assets/Resource Groups")]
    public sealed class AssetGroups : ScriptableObject
    {
        public ResourceAddressMode AddressMode = ResourceAddressMode.FullPath;
        public BundleNamingMode BundleNaming = BundleNamingMode.StableHash;
        public ResourceGroup[] Groups = Array.Empty<ResourceGroup>();
    }
    public enum BundleNamingMode { StableHash, GroupName }
    public enum ResourceAddressMode { FullPath, GroupAndAssetName }
    [Serializable]
    public sealed class ResourceGroup
    {
        public string Id = "";
        [Tooltip("Display name used by readable bundle names and group/name addresses. Empty uses Id.")]
        public string Name = "";
        [Tooltip("Recursively collected at validation/build time.")]
        public UnityEngine.Object[] Folders = Array.Empty<UnityEngine.Object>();
        public ResourceAddress[] Assets = Array.Empty<ResourceAddress>();
        internal string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }
    [Serializable]
    public sealed class ResourceAddress
    {
        public UnityEngine.Object Asset;
    }
}
