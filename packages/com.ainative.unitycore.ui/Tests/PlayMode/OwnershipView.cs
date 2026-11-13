using System;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class OwnershipView : UIView
    {
        public static Action Enabled;
        private void OnEnable()
        {
            if (Enabled != null) { Enabled(); }
        }
    }
}
