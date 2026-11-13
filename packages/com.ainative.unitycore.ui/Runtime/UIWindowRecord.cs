using System;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    internal sealed class UIWindowRecord
    {
        internal readonly Type ViewType;
        internal UIHandle Handle;
        internal long LastSession = 1;
        internal long CacheOrder;
        internal readonly UICachePolicy CachePolicy;
        internal bool Cached;
        internal bool Opening;
        internal bool Releasing;
        internal readonly UITransitionOptions TransitionOptions;
        internal bool Transitioning;
        internal UITransition Transition;
        internal UniTaskCompletionSource<UIResult> Cleanup;
        internal readonly AssetScope Scope;
        internal readonly UIRole Role;
        internal readonly bool OpaqueFullscreen;
        internal readonly bool AllowHudInput;
        internal UnityEngine.GameObject LastSelection;
        internal UnityEngine.GameObject ReturnSelection;
        internal AssetInstance Instance;
        internal UIView View;
        internal UIPresenter Presenter;
        internal bool Bound;
        internal bool Resumed;
        internal bool NeedsResume;
        internal bool Visible;
        internal bool ScopeClosed;
        internal bool CleanupFailed;
        internal UniTaskCompletionSource<UIResult> Closing;

        internal UIWindowRecord(Type viewType, UIHandle handle, AssetScope scope, UIRole role,
            bool opaqueFullscreen, bool allowHudInput, UICachePolicy cachePolicy, UITransitionOptions transition)
        {
            ViewType = viewType;
            Handle = handle;
            Scope = scope;
            Role = role;
            OpaqueFullscreen = opaqueFullscreen;
            AllowHudInput = allowHudInput;
            CachePolicy = cachePolicy;
            TransitionOptions = transition;
        }
    }
}
