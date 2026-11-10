using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [Serializable]
    internal sealed class DeploymentSnapshot
    {
        public int FormatVersion = 1;
        public string CatalogSha256;
        public string Source;
        public List<DeploymentFileDigest> Files = new List<DeploymentFileDigest>();

        internal static string Prepare(string source, AssetCatalog catalog, string digest)
        {
            string destination = Path.Combine(DeploymentFiles.WorkDirectory, digest);
            DeploymentFiles.RejectLinks(destination);
            string staging = Path.Combine(DeploymentFiles.WorkDirectory, "evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            AssetPlayerBuildSupport.ValidatePreservation(source, out var metadata);
            var snapshot = new DeploymentSnapshot { CatalogSha256 = digest, Source = source };
            foreach (string name in EvidenceNames(catalog, metadata))
            {
                string path = Path.Combine(source, name);
                DeploymentFiles.RejectLinks(path);
                File.Copy(path, Path.Combine(staging, name));
                snapshot.Files.Add(new DeploymentFileDigest { Name = name, Sha256 = AssetBuilder.FileHash(Path.Combine(staging, name)) });
            }
            File.WriteAllText(Path.Combine(staging, "deployment.json"), JsonUtility.ToJson(snapshot, true));
            Validate(staging, digest);
            if (Directory.Exists(destination)) { DeploymentFiles.ReplaceDirectory(staging, destination, staging + "-previous"); }
            else { AssetBuilder.Publish(staging, destination); Directory.Delete(staging, true); }
            return destination;
        }

        internal static string ValidateDeployed()
        {
            DeploymentFiles.ValidateRuntime(DeploymentFiles.Destination, true);
            string digest = AssetBuilder.FileHash(Path.Combine(DeploymentFiles.Destination, "catalog.json"));
            string snapshot = Path.Combine(DeploymentFiles.WorkDirectory, digest);
            Validate(snapshot, digest);
            return snapshot;
        }

        internal static AssetBuildMetadata Validate(string root, string expectedDigest)
        {
            DeploymentFiles.RejectLinks(root);
            string record = Path.Combine(root, "deployment.json");
            DeploymentFiles.RejectLinks(record);
            if (!File.Exists(record)) { throw new AssetException("MissingDeploymentEvidence", "Deploy content again; missing snapshot: " + root); }
            var snapshot = JsonUtility.FromJson<DeploymentSnapshot>(File.ReadAllText(record));
            if (snapshot == null || snapshot.FormatVersion != 1 || snapshot.CatalogSha256 != expectedDigest || snapshot.Files == null)
            { throw new AssetException("InvalidDeploymentEvidence", root); }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in snapshot.Files)
            {
                if (file == null || string.IsNullOrEmpty(file.Name) || file.Name.Contains("..") || file.Name != Path.GetFileName(file.Name)
                    || file.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !names.Add(file.Name))
                { throw new AssetException("InvalidDeploymentEvidence", "Invalid evidence file name."); }
                string path = Path.Combine(root, file.Name);
                DeploymentFiles.RejectLinks(path);
                if (!File.Exists(DeploymentFiles.IoPath(path)) || AssetBuilder.FileHash(path) != file.Sha256)
                { throw new AssetException("DeploymentEvidenceMismatch", path); }
            }
            AssetPlayerBuildSupport.ValidatePreservation(root, out var metadata);
            var catalog = JsonUtility.FromJson<AssetCatalog>(File.ReadAllText(Path.Combine(root, "catalog.json")));
            if (AssetBuilder.FileHash(Path.Combine(root, "catalog.json")) != expectedDigest || !names.SetEquals(EvidenceNames(catalog, metadata)))
            { throw new AssetException("DeploymentEvidenceMismatch", "Evidence does not match the deployed catalog."); }
            return metadata;
        }

        private static IEnumerable<string> EvidenceNames(AssetCatalog catalog, AssetBuildMetadata metadata)
        {
            var names = new List<string> { "catalog.json", "build-metadata.json", metadata.PreservationFile };
            if (metadata.Backend == AssetBuildBackend.Builtin)
            { names.AddRange(catalog.Bundles.Select(b => b.Name + ".manifest")); }
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            { throw new AssetException("DeploymentFileCollision", "Preservation and catalog names overlap."); }
            return names;
        }
    }

    [Serializable]
    internal sealed class DeploymentFileDigest
    {
        public string Name;
        public string Sha256;
    }
}
