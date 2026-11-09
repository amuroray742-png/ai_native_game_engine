using System;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [CreateAssetMenu(menuName = "AI Native/Assets/Resource Groups")]
    public sealed class AssetGroups : ScriptableObject
    {
        public ResourceGroup[] Groups = Array.Empty<ResourceGroup>();
    }
    [Serializable]
    public sealed class ResourceGroup
    {
        public string Id = "";
        public ResourceAddress[] Assets = Array.Empty<ResourceAddress>();
    }
    [Serializable]
    public sealed class ResourceAddress
    {
        public string Address = "";
        public UnityEngine.Object Asset;
    }
}
