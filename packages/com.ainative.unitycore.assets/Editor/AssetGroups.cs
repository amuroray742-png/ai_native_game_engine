using System;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [CreateAssetMenu(menuName = "AI Native/Assets/Resource Groups")]
    public sealed class AssetGroups : ScriptableObject
    {
        [Tooltip("Version used by the next build. Incremented and saved only after publication.")]
        public int Version = 1;
        public string OutputDirectory = "{projectDir}/BundleBuildOutput/{version}";
        [Tooltip("Editor module creation only. AssetBundle uses deployed content; AssetDatabase reads source assets. Player always uses bundles.")]
        public EditorAssetLoadMode EditorLoadMode = EditorAssetLoadMode.AssetBundle;
        public AssetBuildBackend BuildBackend = AssetBuildBackend.ScriptableBuildPipeline;
        public ResourceAddressMode AddressMode = ResourceAddressMode.FullPath;
        public BundleNamingMode BundleNaming = BundleNamingMode.StableHash;
        public ResourceGroup[] Groups = Array.Empty<ResourceGroup>();
        [Tooltip("Zero disables the warning. Bytes on disk, not runtime memory.")]
        public long BundleWarningBytes;
        public long TotalWarningBytes;
        public AssetShaderSettings Shaders;
        [SerializeField, HideInInspector] private string _lastBuildPath = "";
        [SerializeField, HideInInspector] private int _lastBuildVersion;
        [SerializeField, HideInInspector] private string _lastBuildCatalogSha256 = "";
        public string LastBuildPath => _lastBuildPath;
        public int LastBuildVersion => _lastBuildVersion;
        public string LastBuildCatalogSha256 => _lastBuildCatalogSha256;

        internal void RecordPublishedBuild(string path, int version, string catalogSha256)
        {
            string project = System.IO.Path.GetDirectoryName(Application.dataPath);
            string relative = System.IO.Path.GetRelativePath(project, path);
            _lastBuildPath = relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar)
                && !System.IO.Path.IsPathRooted(relative) ? relative.Replace('\\', '/') : path;
            _lastBuildVersion = version;
            _lastBuildCatalogSha256 = catalogSha256;
        }
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
        public ResourceFilter Filter = new ResourceFilter();
        public PlatformResourceFilter[] PlatformFilters = Array.Empty<PlatformResourceFilter>();
        internal string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }
    [Serializable]
    public sealed class ResourceAddress
    {
        public UnityEngine.Object Asset;
    }
}
