using System;

namespace AiNative.UnityCore.UI
{
    public enum UIError
    {
        None, NotRunning, NotRegistered, AlreadyOpen, InvalidRole, InvalidHandle,
        NotTopPage, NoBackTarget, NavigationBusy, ReentrantCall, StackLimitReached, WindowBusy
    }

    public readonly struct UIResult
    {
        private readonly bool _initialized;
        private readonly UIError _error;
        public UIError Error => _initialized ? _error : UIError.NotRunning;
        public bool Succeeded => _initialized && _error == UIError.None;
        private UIResult(UIError error) { _initialized = true; _error = error; }
        public static UIResult Success() => new UIResult(UIError.None);
        public static UIResult Rejected(UIError error)
        {
            if (error == UIError.None) throw new ArgumentException("Rejection requires an error.", nameof(error));
            return new UIResult(error);
        }
    }

    public readonly struct UIOpenResult<TView> where TView : UIView
    {
        private readonly UIResult _result;
        private readonly TView _view;
        private readonly UIHandle _handle;
        public UIError Error => _result.Error;
        public bool Succeeded => _result.Succeeded;
        public TView View => Succeeded ? _view : throw new InvalidOperationException("Open was rejected.");
        public UIHandle Handle => Succeeded ? _handle : throw new InvalidOperationException("Open was rejected.");
        private UIOpenResult(UIResult result, TView view, UIHandle handle)
        { _result = result; _view = view; _handle = handle; }
        public static UIOpenResult<TView> Rejected(UIError error) =>
            new UIOpenResult<TView>(UIResult.Rejected(error), null, default);
        internal static UIOpenResult<TView> Success(TView view, UIHandle handle) =>
            new UIOpenResult<TView>(UIResult.Success(), view, handle);
    }
}
