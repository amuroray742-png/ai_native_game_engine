using AiNative.UnityCore.UI;
using UnityEngine;

namespace AiNative.UIDemo
{
    // Attached only by the opt-in Player contract checks while the window is inactive.
    public sealed class UIDemoSortingActivation : MonoBehaviour
    {
        public Canvas Injected { get; private set; }

        private void OnEnable()
        {
            Injected = GetComponent<UIView>().Visual.gameObject.AddComponent<Canvas>();
            Injected.overrideSorting = true;
            Injected.sortingOrder = 99;
        }
    }
}
