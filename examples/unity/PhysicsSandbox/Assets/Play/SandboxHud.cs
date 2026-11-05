#nullable enable
using AiNative.Physics;
using UnityEngine;
using UnityEngine.UI;
using NVector = System.Numerics.Vector3;

namespace AiNative.Examples.PhysicsPlay
{
    public sealed class SandboxHud : MonoBehaviour
    {
        [SerializeField] private Text status = null!, details = null!, feedback = null!, pauseLabel = null!;
        [SerializeField] private Button sphere = null!, box = null!, impulseX = null!, impulseY = null!, remove = null!;
        [SerializeField] private Button pause = null!, save = null!, load = null!, restart = null!;
        [SerializeField] private InputField target = null!;
        private SandboxScene? _scene;
        internal InputField Target => target;

        internal void Bind(SandboxScene scene)
        {
            _scene = scene;
            sphere.onClick.AddListener(SpawnSphere);
            box.onClick.AddListener(SpawnBox);
            impulseX.onClick.AddListener(PushX);
            impulseY.onClick.AddListener(PushY);
            remove.onClick.AddListener(Remove);
            pause.onClick.AddListener(TogglePause);
            save.onClick.AddListener(scene.SaveSlot);
            load.onClick.AddListener(scene.LoadSlot);
            restart.onClick.AddListener(scene.NewWorld);
            Show(scene);
        }

        internal void Unbind()
        {
            if (_scene == null) { return; }
            sphere.onClick.RemoveListener(SpawnSphere);
            box.onClick.RemoveListener(SpawnBox);
            impulseX.onClick.RemoveListener(PushX);
            impulseY.onClick.RemoveListener(PushY);
            remove.onClick.RemoveListener(Remove);
            pause.onClick.RemoveListener(TogglePause);
            save.onClick.RemoveListener(_scene.SaveSlot);
            load.onClick.RemoveListener(_scene.LoadSlot);
            restart.onClick.RemoveListener(_scene.NewWorld);
            _scene = null;
        }

        internal void Show(SandboxScene scene)
        {
            status.text = (scene.Paused ? "Paused" : "Live") + "     /     Tick " + scene.Session.Tick;
            details.text = $"{scene.Display.Count} bodies · {scene.Session.Pending} queued · {scene.Contacts} contacts";
            feedback.text = scene.Feedback;
            pauseLabel.text = scene.Paused ? "Resume" : "Pause";
            bool valid = ulong.TryParse(target.text, out _);
            impulseX.interactable = impulseY.interactable = remove.interactable = valid;
        }

        private void SpawnSphere() => _scene!.Spawn(ShapeKind.Sphere);
        private void SpawnBox() => _scene!.Spawn(ShapeKind.Box);
        private void TogglePause() => _scene!.Paused = !_scene.Paused;
        private void PushX() { if (ulong.TryParse(target.text, out ulong id)) { _scene!.Impulse(id, NVector.UnitX); } }
        private void PushY() { if (ulong.TryParse(target.text, out ulong id)) { _scene!.Impulse(id, NVector.UnitY); } }
        private void Remove() { if (ulong.TryParse(target.text, out ulong id)) { _scene!.Remove(id); } }
    }
}
