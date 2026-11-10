using System;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    public enum AssetBuildBackend { ScriptableBuildPipeline = 0, Builtin = 1 }

    public sealed class AssetBuildOptions
    {
        public bool UseCache = true;
    }

    [Serializable]
    internal sealed class AssetBuildMetadata
    {
        public int FormatVersion = 1;
        public AssetBuildBackend Backend;
        public string UnityVersion = Application.unityVersion;
        public string SbpVersion = "";
        public bool UseCache;
        public string PreservationFile = "";
        public string PreservationSha256 = "";
    }
}
