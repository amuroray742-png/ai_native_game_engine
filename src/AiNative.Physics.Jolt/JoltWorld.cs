using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AiNative.Physics.Jolt
{
    internal sealed class JoltWorld : IPhysicsWorld
    {
        private readonly WorldHandle _handle;
        private readonly int _workerThreads;
        private readonly BodyRegistry _bodies;
        private bool _faulted;

        internal JoltWorld(WorldHandle handle, int workerThreads, BodyRegistry bodies)
        {
            _handle = handle;
            _workerThreads = workerThreads;
            _bodies = bodies;
        }

        public void Add(BodyDefinition definition) => AddBody(definition, null);
        internal void AddRestored(BodyEntry entry) => AddBody(entry.Definition, entry.BodyId);

        private void AddBody(BodyDefinition definition, uint? restoredId)
        {
            EnsureAvailable();
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            var native = new JoltNative.Definition
            {
                Shape = (int)definition.Shape,
                Dynamic = definition.Dynamic ? 1 : 0,
                Size = definition.Size,
                Position = definition.Position,
                Rotation = definition.Rotation,
                Mass = definition.Mass,
                Friction = definition.Friction,
                Restitution = definition.Restitution
            };
            // Reserve managed storage first; native add rolls back its own resources on failure.
            BodyEntry entry = _bodies.Reserve(definition, restoredId);
            try
            {
                JoltNative.Check(JoltNative.aj_add(_handle, entry.BodyId, in native));
            }
            catch
            {
                _bodies.CancelReservation(entry);
                throw;
            }
        }

        public void Remove(ulong id)
        {
            EnsureAvailable();
            BodyEntry entry = _bodies.Find(id);
            JoltNative.Check(JoltNative.aj_remove(_handle, entry.BodyId));
            _bodies.Remove(entry);
        }

        public void ApplyImpulse(ulong id, Vector3 impulse)
        {
            EnsureAvailable();
            if (!BodyDefinition.Finite(impulse))
            {
                throw new ArgumentException("冲量必须有限。");
            }
            BodyEntry entry = _bodies.Find(id);
            if (!entry.Definition.Dynamic)
            {
                throw new InvalidOperationException("Static body cannot receive impulse.");
            }
            JoltNative.Check(JoltNative.aj_impulse(_handle, entry.BodyId, impulse));
        }

        public IReadOnlyList<BodyState> GetBodies()
        {
            EnsureAvailable();
            var native = new JoltNative.BodyView[_bodies.Count];
            JoltNative.Check(JoltNative.aj_bodies(_handle, native, native.Length, out int count));
            if (count != native.Length)
            {
                throw new InvalidOperationException("Native body count differs from managed ownership.");
            }
            var result = new BodyState[count];
            for (int index = 0; index < count; index++)
            {
                JoltNative.BodyView body = native[index];
                result[index] = new BodyState(_bodies.EntityFor(body.BodyId), body.Position, body.Rotation,
                    body.Velocity, body.AngularVelocity, body.Active != 0, body.Dynamic != 0);
            }
            Array.Sort(result, (first, second) => first.Id.CompareTo(second.Id));
            return Array.AsReadOnly(result);
        }

        public IReadOnlyList<PhysicsContact> Step(float seconds)
        {
            EnsureAvailable();
            if (!BodyDefinition.Finite(seconds) || seconds <= 0 || seconds > 1)
            {
                throw new InvalidOperationException("Invalid step duration.");
            }
            try
            {
                JoltNative.Check(JoltNative.aj_step(_handle, seconds));
                JoltNative.Check(JoltNative.aj_contacts(_handle, null, 0, out int count));
                var native = new JoltNative.Contact[Math.Max(1, count)];
                JoltNative.Check(JoltNative.aj_contacts(_handle, native, native.Length, out _));
                IReadOnlyList<PhysicsContact> contacts = ConvertContacts(native, count);
                _bodies.CompleteStep();
                return contacts;
            }
            catch
            {
                // Update or event consumption may already have changed native state.
                _faulted = true;
                throw;
            }
        }

        private IReadOnlyList<PhysicsContact> ConvertContacts(JoltNative.Contact[] contacts, int count)
        {
            var ordered = new SortedSet<(ulong First, ulong Second, uint FirstShape, uint SecondShape, int Kind)>();
            for (int index = 0; index < count; index++)
            {
                JoltNative.Contact contact = contacts[index];
                ulong first = _bodies.EntityFor(contact.First), second = _bodies.EntityFor(contact.Second);
                uint firstShape = contact.FirstShape, secondShape = contact.SecondShape;
                if (second < first)
                {
                    (first, second) = (second, first);
                    (firstShape, secondShape) = (secondShape, firstShape);
                }
                ordered.Add((first, second, firstShape, secondShape, contact.Kind));
            }
            return Array.AsReadOnly(ordered.Select(contact => new PhysicsContact(contact.First, contact.Second,
                contact.FirstShape, contact.SecondShape, contact.Kind == 1)).ToArray());
        }

        public PhysicsSnapshot Capture()
        {
            EnsureAvailable();
            if (_bodies.HasRetiredBodies)
            {
                throw new InvalidOperationException("Advance removed bodies before saving.");
            }
            JoltNative.Check(JoltNative.aj_capture_state(_handle, null, 0, out int length));
            var nativeState = new byte[length];
            JoltNative.Check(JoltNative.aj_capture_state(_handle, nativeState, nativeState.Length, out _));
            return PhysicsSnapshotCodec.Write(_bodies, _workerThreads, nativeState);
        }

        internal void RestoreInternalState(byte[] nativeState)
        {
            EnsureAvailable();
            try
            {
                JoltNative.Check(JoltNative.aj_restore_state(_handle, nativeState, nativeState.Length));
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        private void EnsureAvailable()
        {
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                throw new ObjectDisposedException(nameof(JoltWorld));
            }
            if (_faulted)
            {
                throw new InvalidOperationException("Physics world is faulted; dispose it before continuing.");
            }
        }

        public void Dispose() => _handle.Dispose();
    }
}
