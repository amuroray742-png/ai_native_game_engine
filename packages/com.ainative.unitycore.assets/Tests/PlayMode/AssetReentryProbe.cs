using System;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Tests
{
    public sealed class AssetReentryProbe : MonoBehaviour, IPooledInstanceCallbacks
    {
        internal static Action Enabled;
        internal static Action Rented;
        internal static Action Disabled;
        internal static Action<GameObject> Activated;
        internal static Action Destroyed;
        private void OnDestroy() { Destroyed?.Invoke(); }
        private void OnEnable() { Enabled?.Invoke(); Activated?.Invoke(gameObject); }
        private void OnDisable() { Disabled?.Invoke(); }
        public void OnRent() { Rented?.Invoke(); }
        public void OnReturn() { }
    }
}
