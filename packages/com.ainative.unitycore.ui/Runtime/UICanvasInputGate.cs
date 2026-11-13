using UnityEngine;

namespace AiNative.UnityCore.UI
{
    // Graphic stops inspecting ancestors at an override-sorting Canvas. Keep the
    // window's input policy effective there without rewriting authored sorting.
    internal sealed class UICanvasInputGate : MonoBehaviour, ICanvasRaycastFilter
    {
        internal CanvasGroup Owner;
        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera) =>
            Owner != null && Owner.blocksRaycasts;
    }
}
