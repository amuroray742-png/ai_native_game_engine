using System;
using System.IO;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEngine;

namespace AiNative.Examples.PhysicsPlay.Editor
{
    public static class SandboxContent
    {
        [MenuItem("PhysicsSandbox/Build and deploy presentation content")]
        public static void BuildContent()
        {
            var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/Presentation/ResourceGroups.asset");
            if (groups == null) { throw new InvalidOperationException("Missing Sandbox presentation resource groups."); }
            var build = UnityEngine.Object.Instantiate(groups);
            try
            {
                string output = Path.GetFullPath("BuildArtifacts/Sandbox/" + Guid.NewGuid().ToString("N"));
                AssetBuilder.Build(build, output);
                AssetDeployment.CopyLatestToStreamingAssets(build);
            }
            finally { UnityEngine.Object.DestroyImmediate(build); }
            AssetDatabase.Refresh();
        }
    }
}
