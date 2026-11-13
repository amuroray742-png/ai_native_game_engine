using System;
using UnityEngine.EventSystems;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UISizeProbe : UIBehaviour
    {
        internal static Action Changed;
        protected override void OnRectTransformDimensionsChange() { Changed?.Invoke(); }
    }
}
