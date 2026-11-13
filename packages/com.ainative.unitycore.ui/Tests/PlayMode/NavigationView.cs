using System;

namespace AiNative.UnityCore.UI.Tests
{
    public class NavigationView : UIView
    {
        public static Action<UIView, bool> Activation;
        protected void OnEnable() { Activation?.Invoke(this, true); }
        protected void OnDisable() { Activation?.Invoke(this, false); }
    }
}
