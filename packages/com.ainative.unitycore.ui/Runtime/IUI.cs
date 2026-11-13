using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.UI
{
    public interface IUI
    {
        UniTask<UIOpenResult<TView>> OpenAsync<TView>(CancellationToken cancellationToken = default) where TView : UIView;
        UniTask<UIOpenResult<TView>> PushAsync<TView>(CancellationToken cancellationToken = default) where TView : UIView;
        UniTask<UIResult> CloseAsync(UIHandle handle);
        UniTask<UIResult> ReleaseAsync(UIHandle handle);
        UniTask<UIResult> BackAsync();
        UniTask<UIResult> TrimAsync();
        UniTask<UIResult> ClearAsync();
        UIResult BringToFront(UIHandle handle);
        bool TryGet<TView>(out TView view, out UIHandle handle) where TView : UIView;
        UIDiagnostics GetDiagnostics();
        bool HasModal { get; }
        event Action<bool> ModalStateChanged;
    }
}
