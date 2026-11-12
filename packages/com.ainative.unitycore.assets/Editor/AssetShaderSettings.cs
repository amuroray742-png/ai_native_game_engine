using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.UnityCore.Assets.Editor
{
    [CreateAssetMenu(menuName = "AI Native/Assets/Shader Settings")]
    public sealed class AssetShaderSettings : ScriptableObject
    {
        public bool EnableProcessing;
        public RenderPipelineAsset[] AdditionalPipelines = Array.Empty<RenderPipelineAsset>();
        public Shader[] AdditionalShaders = Array.Empty<Shader>();
        [Tooltip("Saved Editor play-session recordings merged with scanned materials and Required Variants.")]
        public ShaderVariantCollection[] RuntimeCollections = Array.Empty<ShaderVariantCollection>();
        public AssetShaderVariant[] RequiredVariants = Array.Empty<AssetShaderVariant>();
        public AssetShaderExclusion[] Exclusions = Array.Empty<AssetShaderExclusion>();
    }

    [Serializable]
    public sealed class AssetShaderVariant
    {
        public Shader Shader;
        public PassType PassType = PassType.ScriptableRenderPipeline;
        public string PassName = "";
        public string[] Keywords = Array.Empty<string>();
    }

    [Serializable]
    public sealed class AssetShaderExclusion
    {
        public Shader Shader;
        public bool MatchPassType;
        public PassType PassType = PassType.ScriptableRenderPipeline;
        public string PassName = "";
        public string[] RequiredKeywords = Array.Empty<string>();
        public string[] ForbiddenKeywords = Array.Empty<string>();
    }
}
