using UnityEngine;
using UnityEngine.UI;

namespace AiNative.UIDemo
{
    public enum DemoAction { Next, Third, Transparent, Modal, SecondModal, Toast, Back, Hud, Duplicate, Trim, Clear, FaultClear, Scene, CancelTransition }
    public sealed class DemoCommand : MonoBehaviour
    {
        public DemoAction Action;
        public Button Button;
    }
}

