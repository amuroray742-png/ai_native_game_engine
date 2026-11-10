using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    public static class AssetDeployment
    {
        private static bool _copying;

        /// <summary>Deploys the last successful build recorded by this configuration without rebuilding or incrementing its version.</summary>
        public static AssetDeploymentResult CopyLatestToStreamingAssets(AssetGroups settings)
        {
            if (settings == null) { throw new ArgumentNullException(nameof(settings)); }
            if (string.IsNullOrEmpty(settings.LastBuildPath) || string.IsNullOrEmpty(settings.LastBuildCatalogSha256))
            { throw new AssetException("MissingLatestBuild", "Build this configuration first, or deploy an existing directory explicitly."); }
            string source = DeploymentFiles.FullPath(settings.LastBuildPath);
            DeploymentFiles.RejectLinks(Path.Combine(source, "catalog.json"));
            if (AssetBuilder.FileHash(Path.Combine(source, "catalog.json")) != settings.LastBuildCatalogSha256)
            { throw new AssetException("LatestBuildChanged", "The last published catalog changed: " + source); }
            AssetPlayerBuildSupport.ValidatePreservation(source, out var metadata);
            if (metadata.ContentVersion != settings.LastBuildVersion)
            { throw new AssetException("LatestBuildChanged", "The last published version changed: " + source); }
            return CopyToStreamingAssets(source);
        }

        /// <summary>Replaces only StreamingAssets/AiNativeAssets with verified runtime files. Keeps Player preservation evidence outside Assets.</summary>
        public static AssetDeploymentResult CopyToStreamingAssets(string sourceDirectory)
        {
            if (_copying || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
            { throw new InvalidOperationException("Deployment requires Edit Mode with no active deployment or Player build."); }
            string source = DeploymentFiles.FullPath(sourceDirectory);
            string destination = DeploymentFiles.Destination;
            if (DeploymentFiles.ContainsPath(source, destination) || DeploymentFiles.ContainsPath(destination, source)
                || DeploymentFiles.ContainsPath(source, DeploymentFiles.WorkDirectory) || DeploymentFiles.ContainsPath(DeploymentFiles.WorkDirectory, source))
            { throw new AssetException("DeploymentPathOverlap", "Build source overlaps a deployment working or runtime directory."); }
            DeploymentFiles.RejectLinks(destination);
            DeploymentFiles.RejectLinks(DeploymentFiles.WorkDirectory);
            var catalog = DeploymentFiles.ValidateRuntime(source, false);
            DeploymentFiles.RejectLinks(Path.Combine(source, "build-metadata.json"));
            AssetPlayerBuildSupport.ValidatePreservation(source, out var metadata);
            string digest = AssetBuilder.FileHash(Path.Combine(source, "catalog.json"));
            _copying = true;
            string staging = Path.Combine(DeploymentFiles.WorkDirectory, "runtime-" + Guid.NewGuid().ToString("N"));
            try
            {
                DeploymentSnapshot.Prepare(source, catalog, digest);
                Directory.CreateDirectory(staging);
                foreach (string name in DeploymentFiles.RuntimeNames(catalog))
                { File.Copy(Path.Combine(source, name), Path.Combine(staging, name)); }
                DeploymentFiles.ValidateRuntime(staging, true);
                if (AssetBuilder.FileHash(Path.Combine(staging, "catalog.json")) != digest)
                { throw new AssetException("DeploymentSourceChanged", "Source catalog changed during copying."); }
                AssetDatabase.StartAssetEditing();
                try { DeploymentFiles.ReplaceDirectory(staging, destination, staging + "-previous"); }
                finally { AssetDatabase.StopAssetEditing(); }
                var result = new AssetDeploymentResult(source, metadata.ContentVersion, destination,
                    catalog.Bundles.Length + 1, catalog.Bundles.Sum(b => b.Size) + new FileInfo(Path.Combine(destination, "catalog.json")).Length);
                Debug.Log(result.ToString());
                return result;
            }
            finally
            {
                _copying = false;
                AssetDatabase.Refresh();
            }
        }

        public static void BatchCopy()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Read(string key)
            {
                int index = Array.IndexOf(args, key);
                if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("-"))
                { throw new ArgumentException("Missing argument " + key); }
                return args[index + 1];
            }
            bool source = args.Contains("-assetSource");
            bool groups = args.Contains("-assetGroups");
            if (source == groups) { throw new ArgumentException("Specify exactly one of -assetGroups or -assetSource."); }
            if (source) { CopyToStreamingAssets(Read("-assetSource")); }
            else { CopyLatestToStreamingAssets(AssetDatabase.LoadAssetAtPath<AssetGroups>(Read("-assetGroups"))); }
        }
    }

    public sealed class AssetDeploymentResult
    {
        public string Source { get; }
        public int Version { get; }
        public string Destination { get; }
        public int FileCount { get; }
        public long TotalBytes { get; }
        internal AssetDeploymentResult(string source, int version, string destination, int fileCount, long totalBytes)
        { Source = source; Version = version; Destination = destination; FileCount = fileCount; TotalBytes = totalBytes; }
        public override string ToString() => "Deployed version " + Version + " from " + Source + " to " + Destination
            + " (" + FileCount + " files, " + TotalBytes + " bytes).";
    }
}
