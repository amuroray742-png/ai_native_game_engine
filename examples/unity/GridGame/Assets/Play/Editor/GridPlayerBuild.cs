using System;
using System.IO;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.Examples.GridGame.Play.Editor
{
    public static class GridPlayerBuild
    {
        public static void Mono() => Build(ScriptingImplementation.Mono2x);
        public static void Il2Cpp() => Build(ScriptingImplementation.IL2CPP);

        private static void Build(ScriptingImplementation backend)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-playerOutput");
            if (index < 0 || index + 1 >= arguments.Length)
            { throw new ArgumentException("-playerOutput is required."); }
            string output = Path.GetFullPath(arguments[index + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/GridGame.unity");
            if (UnityEngine.Object.FindFirstObjectByType<GridPlayerSmoke>() == null)
            {
                var marker = new GameObject("Grid Player Acceptance");
                marker.AddComponent<GridPlayerSmoke>();
                EditorSceneManager.SaveScene(scene);
            }

            var target = NamedBuildTarget.Standalone;
            var previousBackend = PlayerSettings.GetScriptingBackend(target);
            var previousStripping = PlayerSettings.GetManagedStrippingLevel(target);
            bool previousDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            try
            {
                PlayerSettings.SetScriptingBackend(target, backend);
                PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Minimal);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/GridGame.unity" },
                    locationPathName = output,
                    target = BuildTarget.StandaloneWindows64,
                    options = backend == ScriptingImplementation.Mono2x ? BuildOptions.Development : BuildOptions.None
                };
                BuildReport report = AssetPlayerBuildSupport.BuildDeployedPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                { throw new InvalidOperationException("GridGame Player build failed: " + report.summary.result); }
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(target, previousBackend);
                PlayerSettings.SetManagedStrippingLevel(target, previousStripping);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, previousApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, previousDefaultApis);
            }
        }
    }
}
