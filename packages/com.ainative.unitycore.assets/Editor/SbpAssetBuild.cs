using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    internal static class SbpAssetBuild
    {
        internal const string SupportedVersion = "2.6.2";

        internal static Dictionary<string, HashSet<string>> Build(AssetBuildPlan plan, string staging, AssetBuildOptions options, AssetBuildMetadata metadata, out Dictionary<string, uint> crcs)
        {
            string version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ContentPipeline).Assembly).version;
            if (version != SupportedVersion) { throw new AssetException("SbpVersionMismatch", "Validated SBP version required: " + SupportedVersion + "; installed: " + version); }
            metadata.SbpVersion = version;
            metadata.PreservationFile = "link.xml";
            var parameters = new BundleBuildParameters(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, staging)
            {
                BundleCompression = BuildCompression.LZ4, UseCache = options.UseCache, WriteLinkXML = true,
                CacheServerHost = "", NonRecursiveDependencies = false
            };
            var capture = new SbpEvidenceCapture();
            var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
            tasks.Add(capture);
            bool previousSlim = ScriptableBuildPipeline.slimWriteResults;
            try
            {
                // This flag also participates in SBP's write cache key: slim cache entries cannot masquerade as full evidence.
                ScriptableBuildPipeline.slimWriteResults = false;
                var code = ContentPipeline.BuildAssetBundles(parameters, new BundleBuildContent(plan.Definitions), out var results, tasks);
                if (code < ReturnCode.Success || results == null || capture.Evidence == null)
                { throw new AssetException("SbpBuildFailed", "SBP returned " + code + "; retained " + staging); }
            }
            finally { ScriptableBuildPipeline.slimWriteResults = previousSlim; }
            File.WriteAllText(Path.Combine(staging, "sbp-write-results.json"), JsonUtility.ToJson(capture.Evidence, true));
            SbpBuildEvidence.Validate(plan, capture.Evidence, staging);
            if (!File.Exists(Path.Combine(staging, "link.xml"))) { throw new AssetException("MissingPreservationFile", "SBP did not generate link.xml."); }
            var allowed = new HashSet<string>(capture.Evidence.Bundles.Select(x => x.Name), StringComparer.Ordinal)
            { "link.xml", "buildlogtep.json", "sbp-write-results.json", "actual-ownership.tsv", "planned-ownership.tsv", "excluded.txt", "ownership-errors.txt" };
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                if (Path.GetDirectoryName(file) != staging || !allowed.Contains(Path.GetFileName(file)))
                { throw new AssetException("UnexpectedBuildOutput", file); }
            }
            crcs = capture.Evidence.Bundles.ToDictionary(x => x.Name, x => x.Crc, StringComparer.Ordinal);
            return capture.Evidence.Bundles.ToDictionary(x => x.Name, x => new HashSet<string>(x.Dependencies, StringComparer.Ordinal), StringComparer.Ordinal);
        }
    }

    internal sealed class SbpEvidenceCapture : IBuildTask
    {
        public int Version => 1;
#pragma warning disable 649
        [InjectContext(ContextUsage.In)] private IBundleWriteData _write;
        [InjectContext(ContextUsage.In)] private IBundleBuildResults _results;
#pragma warning restore 649
        internal SbpBuildEvidence Evidence;
        public ReturnCode Run()
        {
            if (!_write.FileToBundle.Keys.ToHashSet().SetEquals(_results.WriteResults.Keys))
            { throw new AssetException("ReportMappingIncomplete", "SBP file map and write results differ."); }
            Evidence = new SbpBuildEvidence();
            foreach (var pair in _results.BundleInfos.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                Evidence.Bundles.Add(new SbpBundleEvidence { Name = pair.Key, Dependencies = pair.Value.Dependencies, Crc = pair.Value.Crc });
            }
            foreach (var pair in _results.WriteResults.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var file = new SbpFileEvidence { File = pair.Key, Bundle = _write.FileToBundle[pair.Key] };
                foreach (var item in pair.Value.serializedObjects)
                {
                    var id = item.serializedObject;
                    file.Objects.Add(new SbpObjectEvidence { Guid = id.guid.ToString(), LocalId = id.localIdentifierInFile,
                        FileType = id.fileType.ToString(), FilePath = id.filePath, SourcePath = AssetDatabase.GUIDToAssetPath(id.guid.ToString()) });
                }
                Evidence.Files.Add(file);
            }
            return ReturnCode.Success;
        }
    }
}
