using System;
using System.Collections.Generic;
using AiNative.Core;
using AiNative.Physics;
using UnityEngine;
using Object = UnityEngine.Object;
using NVector = System.Numerics.Vector3;
using NRotation = System.Numerics.Quaternion;

namespace AiNative.Examples.PhysicsPlay
{
    internal sealed class SandboxDisplay : IDisposable
    {
        internal readonly GameObject Root;
        private readonly Material _material;
        private readonly Dictionary<ulong, GameObject> _objects = new Dictionary<ulong, GameObject>();
        private readonly List<ulong> _removed = new List<ulong>();
        private readonly HashSet<ulong> _visible = new HashSet<ulong>();
        internal GameObject Body(ulong id) => _objects[id];
        internal int Count => _objects.Count;
        internal long FromTick { get; private set; }
        internal long ToTick { get; private set; }
        internal float Fraction { get; private set; }

        internal SandboxDisplay(Transform parent, Material material)
        {
            _material = material;
            Root = new GameObject("Physics bodies");
            Root.SetActive(false);
            Root.transform.SetParent(parent, false);
        }

        internal void Draw(Observation<IReadOnlyList<ObservedBody>> fromFrame, Observation<IReadOnlyList<ObservedBody>> toFrame, float fraction)
        {
            var previous = fromFrame.State;
            var current = toFrame.State;
            _visible.Clear();
            // Both observations are sorted by stable ID. Skip bodies removed between the two ticks.
            int prior = 0;
            foreach (var body in current)
            {
                _visible.Add(body.Id);
                if (!_objects.TryGetValue(body.Id, out var item))
                {
                    item = GameObject.CreatePrimitive(body.Shape == ShapeKind.Box ? PrimitiveType.Cube : PrimitiveType.Sphere);
                    item.name = "Body " + body.Id;
                    item.transform.SetParent(Root.transform, false);
                    var collider = item.GetComponent<Collider>();
                    collider.enabled = false;
                    Object.Destroy(collider);
                    var size = Vector(body.Size);
                    item.transform.localScale = body.Shape == ShapeKind.Box ? 2 * size : Vector3.one * (2 * size.x);
                    var tint = new MaterialPropertyBlock();
                    tint.SetColor("_BaseColor", !body.Dynamic ? new Color(.20f,.29f,.36f) :
                        body.Shape == ShapeKind.Sphere ? new Color(.23f,.82f,.74f) : new Color(.98f,.57f,.32f));
                    item.GetComponent<Renderer>().sharedMaterial = _material;
                    item.GetComponent<Renderer>().SetPropertyBlock(tint);
                    _objects.Add(body.Id, item);
                }
                while (prior < previous.Count && previous[prior].Id < body.Id) { prior++; }
                var from = prior < previous.Count && previous[prior].Id == body.Id ? previous[prior] : body;
                item.transform.SetPositionAndRotation(Vector3.Lerp(Vector(from.Position), Vector(body.Position), fraction),
                    Quaternion.Slerp(Rotation(from.Rotation), Rotation(body.Rotation), fraction));
            }
            _removed.Clear();
            foreach (ulong id in _objects.Keys)
            {
                if (!_visible.Contains(id)) { _removed.Add(id); }
            }
            foreach (ulong id in _removed)
            {
                _objects[id].SetActive(false);
                Object.Destroy(_objects[id]);
                _objects.Remove(id);
            }
            FromTick = fromFrame.Tick;
            ToTick = toFrame.Tick;
            Fraction = fraction;
        }
        internal static Vector3 Vector(NVector value) => new Vector3(value.X, value.Y, value.Z);
        internal static Quaternion Rotation(NRotation value) => new Quaternion(value.X, value.Y, value.Z, value.W);
        public void Dispose()
        {
            Root.SetActive(false);
            Object.Destroy(Root);
        }
    }
}
