using System;

namespace AiNative.Core
{
    /// <summary>每个模拟独享的 SplitMix64；不依赖运行时的 Random 实现。</summary>
    public sealed class DeterministicRandom
    {
        public const string Algorithm = "splitmix64-v1";

        public DeterministicRandom(ulong seed)
        {
            State = seed;
        }

        public ulong State { get; private set; }

        public ulong NextUInt64()
        {
            unchecked
            {
                State += 0x9E3779B97F4A7C15UL;
                ulong value = State;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
