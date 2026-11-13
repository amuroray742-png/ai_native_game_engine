using System;
using AiNative.UnityCore.Assets;

namespace AiNative.UnityCore.UI
{
    public enum UIRole { Hud, Page, Modal, Overlay }
    public enum UICachePolicy { Release, Cache }

    public sealed class UIWindowDefinition<TView> where TView : UIView
    {
        public string Address { get; }
        public UIRole Role { get; }
        public Func<TView, IAssets, UIPresenter> PresenterFactory { get; }
        public UICachePolicy CachePolicy { get; }
        public bool OpaqueFullscreen { get; }
        public bool AllowHudInput { get; }
        public UITransitionOptions Transition { get; }

        public UIWindowDefinition(string address, UIRole role,
            Func<TView, IAssets, UIPresenter> presenterFactory,
            UICachePolicy cachePolicy = UICachePolicy.Release,
            bool opaqueFullscreen = false, bool allowHudInput = false,
            UITransitionOptions transition = default)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Address is required.", nameof(address));
            }
            if (presenterFactory == null)
            {
                throw new ArgumentNullException(nameof(presenterFactory));
            }
            if (role < UIRole.Hud || role > UIRole.Overlay)
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }
            if (cachePolicy != UICachePolicy.Release && cachePolicy != UICachePolicy.Cache)
            {
                throw new ArgumentOutOfRangeException(nameof(cachePolicy));
            }
            if (role != UIRole.Page && (opaqueFullscreen || allowHudInput))
            {
                throw new ArgumentException("Only pages can declare page policies.");
            }
            Address = address;
            Role = role;
            PresenterFactory = presenterFactory;
            CachePolicy = cachePolicy;
            OpaqueFullscreen = opaqueFullscreen;
            AllowHudInput = allowHudInput;
            Transition = transition;
        }
    }
}
