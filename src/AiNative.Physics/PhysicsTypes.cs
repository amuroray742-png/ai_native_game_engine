using System;
using System.Collections.Generic;
using System.Numerics;

namespace AiNative.Physics
{
    public enum ShapeKind
    {
        Sphere, Box
    }

    public sealed class BodyDefinition
    {
        public BodyDefinition(ulong id, ShapeKind shape, bool dynamic, Vector3 size, Vector3 position,
            Quaternion rotation, float mass = 1, float friction = 0.5f, float restitution = 0)
        {
            if (id == 0 || (shape != ShapeKind.Sphere && shape != ShapeKind.Box)
                || !Finite(size) || !Finite(position) || size.X <= 0 || size.Y <= 0 || size.Z <= 0
                || !Finite(mass) || mass <= 0 || !Finite(friction) || friction < 0
                || !Finite(restitution) || restitution < 0 || restitution > 1
                || !Finite(rotation.X) || !Finite(rotation.Y) || !Finite(rotation.Z) || !Finite(rotation.W)
                || Math.Abs(rotation.LengthSquared() - 1) > 0.00001f)
            {
                throw new ArgumentException("无效的刚体定义。");
            }
            Id = id;
            Shape = shape;
            Dynamic = dynamic;
            Size = size;
            Position = position;
            Rotation = rotation;
            Mass = mass;
            Friction = friction;
            Restitution = restitution;
        }

        public ulong Id { get; }
        public ShapeKind Shape { get; }
        public bool Dynamic { get; }
        public Vector3 Size { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public float Mass { get; }
        public float Friction { get; }
        public float Restitution { get; }
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    }

    public class BodyState
    {
        public BodyState(ulong id, Vector3 position, Quaternion rotation, Vector3 velocity,
            Vector3 angularVelocity, bool active, bool dynamic)
        {
            Id = id;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
            Active = active;
            Dynamic = dynamic;
        }
        public ulong Id { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Velocity { get; }
        public Vector3 AngularVelocity { get; }
        public bool Active { get; }
        public bool Dynamic { get; }
    }

    /// <summary>活体观察，引用同一世界登记的不可变定义。v2 快照的诊断数据仍使用 BodyState。</summary>
    public sealed class ObservedBody : BodyState
    {
        public ObservedBody(BodyDefinition definition, Vector3 position, Quaternion rotation, Vector3 velocity,
            Vector3 angularVelocity, bool active)
            : base(definition.Id, position, rotation, velocity, angularVelocity, active, definition.Dynamic)
        {
            Shape = definition.Shape;
            Size = definition.Size;
        }
        public ShapeKind Shape { get; }
        public Vector3 Size { get; }
    }

    public sealed class PhysicsContact
    {
        public PhysicsContact(ulong first, ulong second, uint firstShape, uint secondShape, bool started)
        {
            First = first;
            Second = second;
            FirstShape = firstShape;
            SecondShape = secondShape;
            Started = started;
        }
        public ulong First { get; }
        public ulong Second { get; }
        public uint FirstShape { get; }
        public uint SecondShape { get; }
        public bool Started { get; }
    }

    /// <summary>后端自包含的恢复材料，外部不能改变原始字节。</summary>
    public sealed class PhysicsSnapshot
    {
        private readonly byte[] _data;
        public PhysicsSnapshot(string backend, byte[] data, int workerThreads = 0)
        {
            if (workerThreads < 0 || workerThreads > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(workerThreads));
            }
            WorkerThreads = workerThreads;
            Backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _data = (byte[])(data ?? throw new ArgumentNullException(nameof(data))).Clone();
            if (_data.Length == 0 || _data.Length >= 64 * 1024 * 1024)
            {
                throw new ArgumentException("无效物理状态长度。");
            }
        }
        public int WorkerThreads { get; }
        public string Backend { get; }
        public byte[] GetData() => (byte[])_data.Clone();
    }

    public interface IPhysicsWorld : IDisposable
    {
        void Add(BodyDefinition definition);
        void Remove(ulong id);
        void ApplyImpulse(ulong id, Vector3 impulse);
        IReadOnlyList<ObservedBody> GetBodies();
        IReadOnlyList<PhysicsContact> Step(float seconds);
        PhysicsSnapshot Capture();
    }

    public interface IPhysicsWorldFactory
    {
        string Backend { get; }
        void ValidateSnapshot(PhysicsSnapshot snapshot, IReadOnlyList<BodyState>? expectedBodies = null);
        IPhysicsWorld Create(int workerThreads = 0);
        IPhysicsWorld Restore(PhysicsSnapshot snapshot);
    }
}
