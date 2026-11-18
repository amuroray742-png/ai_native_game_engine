using AiNative.UnityCore.UI;
using UnityEngine;
using UnityEngine.UI;

public sealed class DemoControlsView : UIView
{
    [SerializeField] private Text status;
    [SerializeField] private Button[] buttons;

    internal Text Status => status;
    internal Button[] Buttons => buttons;
}
