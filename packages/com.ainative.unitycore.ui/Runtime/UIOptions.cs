using System;
using UnityEngine;

namespace AiNative.UnityCore.UI
{
    public sealed class UIOptions
    {
        public int ClosedCacheCapacity { get; }
        public int MaxPageDepth { get; }
        public Vector2 ReferenceResolution { get; }
        public float MatchWidthOrHeight { get; }
        public Color ModalMaskColor { get; }

        /// <summary>创建不可变显示选项；分辨率须有限且为正，匹配值与颜色各分量须在 0..1。</summary>
        public UIOptions(int closedCacheCapacity = 8, int maxPageDepth = 16,
            Vector2? referenceResolution = null, float matchWidthOrHeight = 0.5f, Color? modalMaskColor = null)
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
            var resolution = referenceResolution ?? new Vector2(1920, 1080);
            var color = modalMaskColor ?? new Color(0, 0, 0, 0.5f);
            if (!Finite(resolution.x) || !Finite(resolution.y) || resolution.x <= 0 || resolution.y <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(referenceResolution));
            }
            if (!UnitRange(matchWidthOrHeight))
            {
                throw new ArgumentOutOfRangeException(nameof(matchWidthOrHeight));
            }
            if (!UnitRange(color.r) || !UnitRange(color.g) || !UnitRange(color.b) || !UnitRange(color.a))
            {
                throw new ArgumentOutOfRangeException(nameof(modalMaskColor));
            }
            ReferenceResolution = resolution;
            MatchWidthOrHeight = matchWidthOrHeight;
            ModalMaskColor = color;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool UnitRange(float value) => Finite(value) && value >= 0 && value <= 1;
    }
}
