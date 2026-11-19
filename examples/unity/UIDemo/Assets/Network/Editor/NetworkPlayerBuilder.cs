using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace AiNative.NetworkDemo.Editor
{
    public static class NetworkPlayerBuilder
    {
        public static void Build()
        {
            var target = NamedBuildTarget.Standalone;
            var previousBackend = PlayerSettings.GetScriptingBackend(target);
            try
            {
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
                var output = Environment.GetEnvironmentVariable("AINATIVE_NETWORK_PLAYER") ??
                    throw new InvalidOperationException("Set AINATIVE_NETWORK_PLAYER to the output executable path.");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Network/NetworkDemo.unity" },
                    locationPathName = Path.GetFullPath(output),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) { throw new BuildFailedException("Network Player build failed."); }
            }
            finally { PlayerSettings.SetScriptingBackend(target, previousBackend); }
        }
    }
}
