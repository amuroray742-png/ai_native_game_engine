using System;
using AiNative.UnityCore.UI;
using UnityEngine.Events;

namespace AiNative.UIDemo
{
    public sealed class DemoPresenter : UIPresenter
    {
        private readonly DemoView _view;
        private readonly DemoModel _model;
        private readonly Action<DemoAction> _dispatch;
        private UnityAction[] _listeners;

        public DemoPresenter(DemoView view, DemoModel model, Action<DemoAction> dispatch)
        { _view = view; _model = model; _dispatch = dispatch; }

        public override void Bind()
        {
            if (_model.IsDisposed) { throw new ObjectDisposedException(nameof(DemoModel)); }
            _view.Caption.text += "  /  SESSION " + _model.Session;
            _listeners = new UnityAction[_view.Commands.Length];
            for (int i = 0; i < _listeners.Length; i++)
            {
                var action = _view.Commands[i].Action;
                _listeners[i] = () => _dispatch(action);
                _view.Commands[i].Button.onClick.AddListener(_listeners[i]);
            }
            // The host acquires this dynamic lease before opening windows and owns it until Clear succeeds.
            _view.Picture.texture = _model.Image;
        }

        public override void Resume() { }
        public override void Suspend() { }
        public override void Unbind()
        {
            if (_model.FailNextUnbind)
            {
                _model.FailNextUnbind = false;
                throw new InvalidOperationException("Demo requested one cleanup failure; retry Clear.");
            }
            if (_view == null || _listeners == null) { return; }
            for (int i = 0; i < _listeners.Length; i++)
            { _view.Commands[i].Button.onClick.RemoveListener(_listeners[i]); }
            _view.Picture.texture = null;
            _listeners = null;
        }
    }
}

