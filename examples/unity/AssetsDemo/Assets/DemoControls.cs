using System;
using System.Threading;
using System.Collections.Generic;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

internal sealed class DemoControls
{
    private readonly UnityAction[] _commands;
    private DemoControlsView _active;
    private readonly EventSystem _events;
    private string _status;
    private bool _enabled;
    private bool _hostActive = true;
    internal UIModule Module { get; }

    internal DemoControls(Transform sceneRoot, string address, params UnityAction[] commands)
    {
        _commands = commands;
        var input = new GameObject("Demo input", typeof(EventSystem), typeof(InputSystemUIInputModule));
        input.transform.SetParent(sceneRoot, false);
        _events = input.GetComponent<EventSystem>();
        Module = new UIModule(new UIOptions(closedCacheCapacity: 0, referenceResolution: new Vector2(900, 650)),
            _events);
        Module.Register(new UIWindowDefinition<DemoControlsView>(address, UIRole.Hud,
            (view, scope) => new Presenter(this, view)));
    }

    internal async UniTask OpenAsync(CancellationToken token)
    {
        var result = await Module.OpenAsync<DemoControlsView>(token);
        if (!result.Succeeded) { throw new InvalidOperationException("Could not open demo controls: " + result.Error); }
    }

    internal void Show(string status, bool enabled)
    {
        _status = status;
        _enabled = enabled;
        if (_active == null) { return; }
        _active.Status.text = _status;
        _active.Visual.gameObject.SetActive(_hostActive);
        foreach (var button in _active.Buttons) { button.interactable = _hostActive && _enabled; }
    }

    internal void SetHostActive(bool active)
    {
        _hostActive = active;
        if (_events != null) { _events.gameObject.SetActive(active); }
        Show(_status, _enabled);
    }

    internal void Disconnect()
    {
        SetHostActive(false);
        Array.Clear(_commands, 0, _commands.Length);
    }

    // Exercise the same raycast target and click binding as the interactive demo.
    internal void ClickForVerification(int index)
    {
        Canvas.ForceUpdateCanvases();
        var button = _active.Buttons[index];
        var rect = (RectTransform)button.transform;
        var pointer = new PointerEventData(_events)
        {
            button = PointerEventData.InputButton.Left,
            position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center))
        };
        var hits = new List<RaycastResult>();
        _events.RaycastAll(pointer, hits);
        if (!button.IsInteractable() || hits.Count == 0 ||
            ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject) != button.gameObject)
        { throw new InvalidOperationException("Cannot click demo control: " + button.name); }
        ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }

    private sealed class Presenter : UIPresenter
    {
        private readonly DemoControls _controls;
        private readonly DemoControlsView _view;
        private readonly UnityAction[] _listeners;
        internal Presenter(DemoControls controls, DemoControlsView view)
        {
            _controls = controls;
            _view = view;
            _listeners = new UnityAction[controls._commands.Length];
            for (int i = 0; i < _listeners.Length; i++)
            {
                int index = i;
                _listeners[i] = () =>
                {
                    if (_controls._hostActive && _controls._enabled && _controls._active == _view)
                    { _controls._commands[index]?.Invoke(); }
                };
            }
        }

        public override void Bind()
        {
            if (_view.Buttons.Length != _controls._commands.Length)
            { throw new InvalidOperationException("Demo controls and commands differ."); }
            for (int i = 0; i < _view.Buttons.Length; i++)
            { _view.Buttons[i].onClick.AddListener(_listeners[i]); }
        }

        public override void Resume()
        {
            _controls._active = _view;
            _controls.Show(_controls._status, _controls._enabled);
        }

        public override void Suspend()
        {
            _controls._active = null;
        }

        public override void Unbind()
        {
            Suspend();
            if (_view == null) { return; }
            for (int i = 0; i < Math.Min(_view.Buttons.Length, _controls._commands.Length); i++)
            { _view.Buttons[i].onClick.RemoveListener(_listeners[i]); }
        }
    }
}
