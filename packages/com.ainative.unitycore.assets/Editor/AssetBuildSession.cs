using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    // One main-thread build owns a configuration until publication and version persistence finish.
    internal sealed class AssetBuildSession : IDisposable
    {
        private static readonly HashSet<int> ActiveSettings = new HashSet<int>();
        private readonly AssetGroups _settings;
        private readonly int _instanceId;
        private readonly string _assetPath;
        private bool _completed;

        internal int Version { get; }
        internal string Destination { get; }
        private static string ProjectDirectory => Path.GetDirectoryName(Application.dataPath);

        private AssetBuildSession(AssetGroups settings, string destination)
        {
            _settings = settings;
            _instanceId = settings.GetInstanceID();
            _assetPath = AssetDatabase.GetAssetPath(settings);
            Version = settings.Version;
            Destination = destination;
        }

        internal static string ResolveOutputDirectory(AssetGroups settings)
        {
            if (settings == null) { throw new ArgumentNullException(nameof(settings)); }
            ValidateVersion(settings.Version);
            string template = settings.OutputDirectory;
            if (string.IsNullOrWhiteSpace(template) || !template.Contains("{version}"))
            {
                throw new ArgumentException("OutputDirectory must contain {version}.");
            }
            string literals = template.Replace("{version}", "").Replace("{projectDir}", "");
            if (literals.Contains("{") || literals.Contains("}"))
            {
                throw new ArgumentException("Unknown output placeholder; supported: {projectDir}, {version} (case-sensitive).");
            }
            string path = template.Replace("{version}", settings.Version.ToString(CultureInfo.InvariantCulture))
                .Replace("{projectDir}", ProjectDirectory);
            return ResolvePath(path);
        }

        internal static AssetBuildSession Begin(AssetGroups settings, string destination)
        {
            ValidateVersion(settings.Version);
            if (ActiveSettings.Contains(settings.GetInstanceID()))
            {
                throw new InvalidOperationException("This resource configuration is already building.");
            }
            var session = new AssetBuildSession(settings, ResolvePath(destination));
            session.EnsureWritable();
            ActiveSettings.Add(session._instanceId);
            return session;
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("Output directory is empty."); }
            string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectDirectory, path));
            if (!string.Equals(Path.GetPathRoot(fullPath), Path.GetPathRoot(ProjectDirectory), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Atomic publication requires output on the project volume.");
            }
            return fullPath;
        }

        private static void ValidateVersion(int version)
        {
            if (version < 1 || version == int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(version), "Version must be positive and leave room for the next build.");
            }
        }

        private void EnsureWritable()
        {
            if (_assetPath.Length == 0) { return; }
            if (!AssetDatabase.IsOpenForEdit(_assetPath) || (File.GetAttributes(_assetPath) & FileAttributes.ReadOnly) != 0)
            {
                throw new IOException("Resource configuration is not writable: " + _assetPath);
            }
            // Probe write access without modifying contents, before spending time building bundles.
            using var stream = File.Open(_assetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }

        internal void Complete()
        {
            if (_completed) { throw new InvalidOperationException("Build version was already committed."); }
            _completed = true;
            _settings.Version = Version + 1;
            if (_assetPath.Length == 0) { return; }
            try
            {
                EditorUtility.SetDirty(_settings);
                EnsureWritable();
                AssetDatabase.SaveAssetIfDirty(_settings);
                if (EditorUtility.IsDirty(_settings)) { throw new IOException("Configuration remains unsaved."); }
            }
            catch (Exception error)
            {
                throw new IOException("Content was published to " + Destination + ", but saving the next version failed. "
                    + "Keep the published content, repair write access and save configuration " + _assetPath + ".", error);
            }
        }

        public void Dispose() => ActiveSettings.Remove(_instanceId);
    }
}
