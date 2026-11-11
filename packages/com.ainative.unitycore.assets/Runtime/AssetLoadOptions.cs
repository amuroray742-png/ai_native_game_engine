using System;

namespace AiNative.UnityCore.Assets
{
    public enum AssetPriority { Low, Normal, High }

    public sealed class AssetLoadOptions
    {
        public AssetPriority Priority { get; }
        public AssetLoadOptions(AssetPriority priority = AssetPriority.Normal)
        {
            if (!Enum.IsDefined(typeof(AssetPriority), priority)) { throw new ArgumentOutOfRangeException(nameof(priority)); }
            Priority = priority;
        }
    }

    public sealed class AssetRuntimeOptions
    {
        public int MaxConcurrentLoads { get; }
        public int InstancesPerFrame { get; }
        public AssetRuntimeOptions(int maxConcurrentLoads = 4, int instancesPerFrame = 2)
        {
            if (maxConcurrentLoads < 1) { throw new ArgumentOutOfRangeException(nameof(maxConcurrentLoads)); }
            if (instancesPerFrame < 1) { throw new ArgumentOutOfRangeException(nameof(instancesPerFrame)); }
            MaxConcurrentLoads = maxConcurrentLoads;
            InstancesPerFrame = instancesPerFrame;
        }
    }
}
