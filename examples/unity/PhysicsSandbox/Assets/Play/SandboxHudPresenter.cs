#nullable enable
using System.Threading;
using AiNative.Physics;
using AiNative.UnityCore.UI;
using NVector = System.Numerics.Vector3;

namespace AiNative.Examples.PhysicsPlay
{
    internal sealed class SandboxHudPresenter : UIPresenter
    {
        private readonly SandboxScene _scene;
        private readonly SandboxHud _view;
        private readonly CancellationToken _lifetime;

        internal SandboxHudPresenter(SandboxHud view, SandboxScene scene, CancellationToken lifetime)
        {
            _scene = scene;
            _lifetime = lifetime;
            _view = view;
        }

        public override void Bind() { }

        public override void Resume()
        {
            if (_lifetime.IsCancellationRequested) { return; }
            _scene.RefreshHud += Refresh;
            _scene.WorldChanged += ResetTarget;
            _scene.Disconnecting += Suspend;
            _view.sphere.onClick.AddListener(SpawnSphere);
            _view.box.onClick.AddListener(SpawnBox);
            _view.impulseX.onClick.AddListener(PushX);
            _view.impulseY.onClick.AddListener(PushY);
            _view.remove.onClick.AddListener(Remove);
            _view.pause.onClick.AddListener(TogglePause);
            _view.save.onClick.AddListener(_scene.SaveSlot);
            _view.load.onClick.AddListener(_scene.LoadSlot);
            _view.restart.onClick.AddListener(_scene.NewWorld);
            Refresh();
        }

        public override void Suspend()
        {
            _scene.RefreshHud -= Refresh;
            _scene.WorldChanged -= ResetTarget;
            _scene.Disconnecting -= Suspend;
            if (_view == null) { return; }
            _view.sphere.onClick.RemoveListener(SpawnSphere);
            _view.box.onClick.RemoveListener(SpawnBox);
            _view.impulseX.onClick.RemoveListener(PushX);
            _view.impulseY.onClick.RemoveListener(PushY);
            _view.remove.onClick.RemoveListener(Remove);
            _view.pause.onClick.RemoveListener(TogglePause);
            _view.save.onClick.RemoveListener(_scene.SaveSlot);
            _view.load.onClick.RemoveListener(_scene.LoadSlot);
            _view.restart.onClick.RemoveListener(_scene.NewWorld);
        }

        public override void Unbind() => Suspend();
        private void ResetTarget() => _view.Target.text = "2";

        private void Refresh()
        {
            if (_scene.Session == null) { return; }
            _view.status.text = (_scene.Paused ? "Paused" : "Live") + "     /     Tick " + _scene.Session.Tick;
            _view.details.text = $"{_scene.Display.Count} bodies · {_scene.Session.Pending} queued · {_scene.Contacts} contacts";
            _view.feedback.text = _scene.Feedback;
            _view.pauseLabel.text = _scene.Paused ? "Resume" : "Pause";
            bool valid = ulong.TryParse(_view.Target.text, out _);
            _view.impulseX.interactable = _view.impulseY.interactable = _view.remove.interactable = valid;
        }
        private void SpawnSphere() => _scene.Spawn(ShapeKind.Sphere);
        private void SpawnBox() => _scene.Spawn(ShapeKind.Box);
        private void TogglePause() => _scene.Paused = !_scene.Paused;
        private void PushX() { if (ulong.TryParse(_view.Target.text, out ulong id)) { _scene.Impulse(id, NVector.UnitX); } }
        private void PushY() { if (ulong.TryParse(_view.Target.text, out ulong id)) { _scene.Impulse(id, NVector.UnitY); } }
        private void Remove() { if (ulong.TryParse(_view.Target.text, out ulong id)) { _scene.Remove(id); } }
    }
}
