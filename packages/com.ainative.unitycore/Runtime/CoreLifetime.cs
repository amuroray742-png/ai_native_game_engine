#nullable enable
using UnityEngine;

namespace AiNative.UnityCore
{
    internal sealed class CoreLifetime : MonoBehaviour
    {
        private Core? _owner;
        internal void Bind(Core owner) { _owner = owner; }
        internal void Unbind() { _owner = null; }
        private void OnApplicationQuit()
        {
            if (_owner != null) { Core.RetireCurrent(); }
        }
        private void OnDestroy()
        {
            Core? owner = _owner;
            _owner = null;
            owner?.OnRootDestroyed();
        }
    }
}
