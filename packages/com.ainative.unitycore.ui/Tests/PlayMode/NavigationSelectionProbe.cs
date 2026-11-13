using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class NavigationSelectionProbe : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        public static Action SelectionChanged;
        public void OnSelect(BaseEventData eventData) { SelectionChanged?.Invoke(); }
        public void OnDeselect(BaseEventData eventData) { SelectionChanged?.Invoke(); }
    }
}
