using System;
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    /// <summary>Builds a Windows Player with preservation evidence from one published content build.</summary>
    public static class AssetPlayerBuildSupport
    {
        private static bool _building;
        internal static string ActiveLinkXml { get; private set; }

        public static BuildReport BuildPlayer(string contentRoot, BuildPlayerOptions options)
        {
            if (_building || BuildPipeline.isBuildingPlayer) { throw new InvalidOperationException("A Player build is already active."); }
            if (options.target != BuildTarget.StandaloneWindows64) { throw new AssetException("UnsupportedPlatform", "Assets currently supports Windows x64 only."); }
            string path = ValidatePreservation(contentRoot, out var metadata);
            _building = true;
            try
            {
                if (metadata.Backend == AssetBuildBackend.Builtin) { options.assetBundleManifestPath = path; }
                else
                {
                    options.assetBundleManifestPath = null;
                    ActiveLinkXml = path;
                }
                return BuildPipeline.BuildPlayer(options);
            }
            finally { ActiveLinkXml = null; _building = false; }
        }

        internal static string ValidatePreservation(string contentRoot, out AssetBuildMetadata metadata)
        {
            contentRoot = Path.GetFullPath(contentRoot);
            string metadataFile = Path.Combine(contentRoot, "build-metadata.json");
            if (!File.Exists(metadataFile)) { throw new AssetException("MissingBuildMetadata", "Rebuild content before building the Player."); }
            metadata = JsonUtility.FromJson<AssetBuildMetadata>(File.ReadAllText(metadataFile));
            if (metadata == null || metadata.FormatVersion != 1 || metadata.UnityVersion != Application.unityVersion
                || !Enum.IsDefined(typeof(AssetBuildBackend), metadata.Backend))
            { throw new AssetException("InvalidBuildMetadata", "Unsupported content build metadata."); }
            var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(contentRoot, "catalog.json")));
            catalog.Validate();
            string file = metadata.PreservationFile;
            if (string.IsNullOrEmpty(file) || file != Path.GetFileName(file) || file.Contains("\\") || file.Contains("/")
                || (metadata.Backend == AssetBuildBackend.ScriptableBuildPipeline && (file != "link.xml" || metadata.SbpVersion != SbpAssetBuild.SupportedVersion)))
            { throw new AssetException("InvalidPreservationFile", "Invalid preservation path or backend version."); }
            string path = Path.Combine(contentRoot, file);
            if (!File.Exists(path)) { throw new AssetException("MissingPreservationFile", path); }
            if (AssetBuilder.FileHash(path) != metadata.PreservationSha256)
            { throw new AssetException("PreservationHashMismatch", path); }
            if (metadata.Backend == AssetBuildBackend.ScriptableBuildPipeline)
            {
                var document = new XmlDocument { XmlResolver = null };
                using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                document.Load(reader);
                if (document.DocumentElement?.Name != "linker") { throw new AssetException("InvalidPreservationFile", "Expected a linker document."); }
            }
            return path;
        }
    }

    internal sealed class AssetLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
            => AssetPlayerBuildSupport.ActiveLinkXml;
    }
}
