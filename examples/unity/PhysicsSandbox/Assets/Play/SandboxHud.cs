#nullable enable
using AiNative.UnityCore.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AiNative.Examples.PhysicsPlay
{
    public sealed class SandboxHud : UIView
    {
        [SerializeField] internal Text status = null!, details = null!, feedback = null!, pauseLabel = null!;
        [SerializeField] internal Button sphere = null!, box = null!, impulseX = null!, impulseY = null!, remove = null!;
        [SerializeField] internal Button pause = null!, save = null!, load = null!, restart = null!;
        [SerializeField] private InputField target = null!;
        internal InputField Target => target;
    }
}
