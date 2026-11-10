using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    internal static class DeploymentFiles
    {
        internal static string ProjectDirectory => Path.GetDirectoryName(Application.dataPath);
        internal static string Destination => Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "AiNativeAssets"));
        internal static string WorkDirectory => Path.GetFullPath(Path.Combine(ProjectDirectory, "BuildArtifacts/Assets/StreamingAssetsDeployment"));

        internal static string IoPath(string path)
        {
            string full = Path.GetFullPath(path);
            // Unity's Mono FileStream does not automatically prefix long Windows paths (File.Copy does).
            if (Application.platform != RuntimePlatform.WindowsEditor || full.Length < 260 || full.StartsWith(@"\\?\")) { return full; }
            return full.StartsWith(@"\\") ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        internal static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("Content directory is empty."); }
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectDirectory, path));
        }

        internal static void RejectLinks(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                string ioPath = IoPath(current);
                if ((File.Exists(ioPath) || Directory.Exists(ioPath)) && (File.GetAttributes(ioPath) & FileAttributes.ReparsePoint) != 0)
                { throw new AssetException("DeploymentPathLink", "Linked content paths are not supported: " + current); }
            }
        }

        internal static bool ContainsPath(string parent, string child)
        {
            parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            child = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
                || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectTreeLinks(string root)
        {
            RejectLinks(root);
            if (!Directory.Exists(root)) { return; }
            foreach (string entry in Directory.GetFileSystemEntries(root))
            {
                RejectLinks(entry);
                if (Directory.Exists(entry)) { RejectTreeLinks(entry); }
            }
        }

        internal static AssetCatalog ValidateRuntime(string root, bool exactFiles)
        {
            RejectLinks(root);
            RejectLinks(Path.Combine(root, "catalog.json"));
            var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(root, "catalog.json")));
            if (catalog == null) { throw new AssetException("InvalidCatalog", "Empty deployment catalog."); }
            catalog.Validate();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "catalog.json" };
            foreach (var bundle in catalog.Bundles)
            {
                if (!names.Add(bundle.Name) || bundle.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                { throw new AssetException("DeploymentFileCollision", bundle.Name); }
                string path = Path.Combine(root, bundle.Name);
                RejectLinks(path);
                if (!File.Exists(IoPath(path)) || new FileInfo(IoPath(path)).Length != bundle.Size || AssetBuilder.FileHash(path) != bundle.Sha256)
                { throw new AssetException("DeploymentBundleMismatch", "Missing or changed bundle: " + path); }
            }
            if (exactFiles)
            {
                if (Directory.GetDirectories(root).Length != 0 || Directory.GetFiles(root)
                    .Any(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !names.Contains(Path.GetFileName(p))))
                { throw new AssetException("UnexpectedDeploymentFile", "Only catalog.json and listed bundles may be deployed: " + root); }
                foreach (string file in Directory.GetFiles(root)) { RejectLinks(file); }
            }
            return catalog;
        }

        internal static string[] RuntimeNames(AssetCatalog catalog)
            => new[] { "catalog.json" }.Concat(catalog.Bundles.Select(b => b.Name)).ToArray();

        internal static void ReplaceDirectory(string staged, string destination, string backup)
        {
            RejectTreeLinks(staged); RejectTreeLinks(destination); RejectLinks(backup);
            // The caller owns these exact directories; never merge with the old content.
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            bool previous = Directory.Exists(destination);
            if (previous) { Directory.Move(destination, backup); }
            try { Directory.Move(staged, destination); }
            catch (Exception error)
            {
                if (previous)
                {
                    try { Directory.Move(backup, destination); }
                    catch (Exception rollback)
                    { throw new AggregateException("Deployment failed; restore the retained backup at " + backup, error, rollback); }
                }
                throw;
            }
            if (previous)
            {
                try { Directory.Delete(backup, true); }
                catch (Exception error)
                { throw new IOException("New content is deployed, but old backup cleanup failed: " + backup, error); }
            }
        }
    }
}
