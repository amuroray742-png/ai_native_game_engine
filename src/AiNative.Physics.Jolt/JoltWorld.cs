using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AiNative.Physics.Jolt
{
    public sealed class JoltWorldFactory : IPhysicsWorldFactory
    {
        public const string Identity = "jolt-5.6.0;e77f175;abi=2;float32;single-thread;deterministic;sse4;capacity=1024";
        public string Backend => Identity;
        public static uint ActiveWorldCount => JoltNative.aj_world_count();

        private static void CheckVersion()
        {
            string? actual = Marshal.PtrToStringAnsi(JoltNative.aj_version());
            if (actual != Identity)
            {
                throw new BadImageFormatException("NativeLibraryVersionMismatch: expected " + Identity + "; actual " + actual);
            }
        }

        public IPhysicsWorld Create()
        {
            CheckVersion();
            JoltNative.Check(JoltNative.aj_create(out WorldHandle handle));
            return new JoltWorld(handle);
        }

        public IPhysicsWorld Restore(PhysicsSnapshot snapshot)
        {
            ValidateSnapshot(snapshot);
            CheckVersion();
            byte[] data = snapshot.GetData();
            JoltNative.Check(JoltNative.aj_restore(data, data.Length, out WorldHandle handle));
            return new JoltWorld(handle);
        }

        /// <summary>只检查自包含元数据，不加载原生库；完整内部状态由候选世界恢复验证。</summary>
        public void ValidateSnapshot(PhysicsSnapshot snapshot, IReadOnlyList<BodyState>? expectedBodies = null)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.Backend != Identity)
            {
                throw new ArgumentException("物理快照后端不匹配。");
            }
            byte[] bytes = snapshot.GetData();
            try
            {
                using var reader = new BinaryReader(new MemoryStream(bytes, false));
                if (reader.ReadUInt32() != 0x324a4e41)
                {
                    throw new ArgumentException("物理格式不匹配。");
                }
                byte[] generations = reader.ReadBytes(1024);
                uint count = reader.ReadUInt32();
                if (generations.Length != 1024 || count > 1024 || bytes.Length <= 1032 + count * 72)
                {
                    throw new ArgumentException("物理状态长度无效。");
                }
                var entities = new HashSet<ulong>();
                var slots = new HashSet<uint>();
                if (expectedBodies != null && expectedBodies.Count != count)
                {
                    throw new ArgumentException("物理诊断对象数量不一致。");
                }
                for (uint index = 0; index < count; index++)
                {
                    ulong id = reader.ReadUInt64();
                    int shape = reader.ReadInt32(), motion = reader.ReadInt32();
                    Vector3 size = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    _ = new BodyDefinition(id, (ShapeKind)shape, motion == 1, size, position, rotation,
                        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    uint bodyId = reader.ReadUInt32(), slot = bodyId & 0x7fffff;
                    if (expectedBodies != null && !expectedBodies.Any(body => body.Id == id && body.Dynamic == (motion == 1)))
                    {
                        throw new ArgumentException("物理诊断对象标识或运动类型不一致。");
                    }
                    if ((motion != 0 && motion != 1) || !entities.Add(id) || slot >= 1024 || !slots.Add(slot)
                        || (bodyId & 0x80000000) != 0 || generations[slot] != (byte)(bodyId >> 23))
                    {
                        throw new ArgumentException("物理 ID 或运动类型无效。");
                    }
                }
            }
            catch (EndOfStreamException error) { throw new ArgumentException("物理状态截断。", nameof(snapshot), error); }
        }
    }

    internal sealed class JoltWorld : IPhysicsWorld
    {
        private readonly WorldHandle _handle;
        internal JoltWorld(WorldHandle handle)
        {
            _handle = handle;
        }

        public void Add(BodyDefinition definition)
        {
            EnsureAlive();
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            var native = new JoltNative.Definition
            {
                Entity = definition.Id,
                Shape = (int)definition.Shape,
                Dynamic = definition.Dynamic ? 1 : 0,
                Size = definition.Size,
                Position = definition.Position,
                Rotation = definition.Rotation,
                Mass = definition.Mass,
                Friction = definition.Friction,
                Restitution = definition.Restitution
            };
            JoltNative.Check(JoltNative.aj_add(_handle, in native));
        }

        public void Remove(ulong id)
        {
            EnsureAlive();
            JoltNative.Check(JoltNative.aj_remove(_handle, id));
        }
        public void ApplyImpulse(ulong id, Vector3 impulse)
        {
            EnsureAlive();
            if (!BodyDefinition.Finite(impulse))
            {
                throw new ArgumentException("冲量必须有限。");
            }
            JoltNative.Check(JoltNative.aj_impulse(_handle, id, impulse));
        }

        public IReadOnlyList<BodyState> GetBodies()
        {
            EnsureAlive();
            JoltNative.Check(JoltNative.aj_bodies(_handle, null, 0, out int count));
            var bodies = new JoltNative.BodyView[count];
            JoltNative.Check(JoltNative.aj_bodies(_handle, bodies, count, out _));
            return Array.AsReadOnly(bodies.Select(body => new BodyState(body.Entity, body.Position, body.Rotation,
                body.Velocity, body.AngularVelocity, body.Active != 0, body.Dynamic != 0)).ToArray());
        }

        public IReadOnlyList<PhysicsContact> Step(float seconds)
        {
            EnsureAlive();
            JoltNative.Check(JoltNative.aj_step(_handle, seconds));
            JoltNative.Check(JoltNative.aj_contacts(_handle, null, 0, out int count));
            // A non-null buffer also drains an empty contact batch.
            var contacts = new JoltNative.Contact[Math.Max(1, count)];
            JoltNative.Check(JoltNative.aj_contacts(_handle, contacts, contacts.Length, out _));
            return Array.AsReadOnly(contacts.Take(count).Select(contact => new PhysicsContact(contact.First,
                contact.Second, contact.FirstShape, contact.SecondShape, contact.Kind == 1)).ToArray());
        }

        public PhysicsSnapshot Capture()
        {
            EnsureAlive();
            JoltNative.Check(JoltNative.aj_save(_handle, null, 0, out int length));
            var bytes = new byte[length];
            JoltNative.Check(JoltNative.aj_save(_handle, bytes, length, out _));
            return new PhysicsSnapshot(JoltWorldFactory.Identity, bytes);
        }

        private void EnsureAlive()
        {
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                throw new ObjectDisposedException(nameof(JoltWorld));
            }
        }
        public void Dispose() => _handle.Dispose();
    }
}
