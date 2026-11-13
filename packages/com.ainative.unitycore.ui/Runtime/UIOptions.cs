using System;
using UnityEngine;

namespace AiNative.UnityCore.UI
{
    public sealed class UIOptions
    {
        public int ClosedCacheCapacity { get; }
        public int MaxPageDepth { get; }
        public Vector2 ReferenceResolution { get; } = new Vector2(1920, 1080);
        public float MatchWidthOrHeight { get; } = 0.5f;
        public Color ModalMaskColor { get; } = new Color(0, 0, 0, 0.5f);

        public UIOptions(int closedCacheCapacity = 8, int maxPageDepth = 16)
        {
            if (closedCacheCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(closedCacheCapacity));
            }
            if (maxPageDepth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPageDepth));
            }
            ClosedCacheCapacity = closedCacheCapacity;
            MaxPageDepth = maxPageDepth;
        }
    }
}
