using UnityEngine;
using System.Threading;

namespace AiNative.UnityCore.UI
{
    internal static class UISafeArea
    {
        internal static Rect Normalize(int width, int height, Rect safeArea)
        {
            if (width <= 0 || height <= 0) { return new Rect(0, 0, 1, 1); }
            float left = Mathf.Clamp01(safeArea.xMin / width);
            float bottom = Mathf.Clamp01(safeArea.yMin / height);
            float right = Mathf.Max(left, Mathf.Clamp01(safeArea.xMax / width));
            float top = Mathf.Max(bottom, Mathf.Clamp01(safeArea.yMax / height));
            return Rect.MinMaxRect(left, bottom, right, top);
        }

        internal static void Apply(UIView view, Rect normalized, CancellationToken token = default)
        {
            view.SafeArea.anchorMin = normalized.min;
            token.ThrowIfCancellationRequested();
            view.SafeArea.anchorMax = normalized.max;
            token.ThrowIfCancellationRequested();
            view.SafeArea.offsetMin = Vector2.zero;
            token.ThrowIfCancellationRequested();
            view.SafeArea.offsetMax = Vector2.zero;
            token.ThrowIfCancellationRequested();
        }
    }
}
