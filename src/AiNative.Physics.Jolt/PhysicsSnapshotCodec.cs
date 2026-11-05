using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AiNative.Physics.Jolt
{
    internal sealed class PhysicsSnapshotData
    {
        internal PhysicsSnapshotData(int workers, byte[] generations, BodyEntry[] entries, byte[] nativeState)
        {
            Workers = workers;
            Generations = generations;
            Entries = entries;
            NativeState = nativeState;
        }
        internal int Workers { get; }
        internal byte[] Generations { get; }
        internal BodyEntry[] Entries { get; }
        internal byte[] NativeState { get; }
    }

    /// <summary>纯托管元数据编解码；离线验证不加载原生库或解释 Jolt 内部流。</summary>
    internal static class PhysicsSnapshotCodec
    {
        private const uint Magic = 0x344a4e41;

        internal static PhysicsSnapshotData Read(PhysicsSnapshot snapshot, IReadOnlyList<BodyState>? expectedBodies = null)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.Backend != JoltWorldFactory.Identity)
            {
                throw new ArgumentException("物理快照后端不匹配，需要重新生成材料。");
            }
            try
            {
                using var stream = new MemoryStream(snapshot.GetData(), false);
                using var reader = new BinaryReader(stream);
                Require(reader.ReadUInt32() == Magic, "物理格式不匹配，需要重新生成材料。");
                Require(reader.ReadUInt32() == snapshot.WorkerThreads, "Physics worker count mismatch.");
                byte[] generations = reader.ReadBytes(BodyRegistry.Capacity);
                uint count = reader.ReadUInt32();
                Require(generations.Length == BodyRegistry.Capacity && count <= BodyRegistry.Capacity
                    && stream.Length > stream.Position + count * 72, "物理状态长度无效。");
                Require(expectedBodies == null || expectedBodies.Count == count, "物理诊断对象数量不一致。");
                BodyEntry[] entries = ReadEntries(reader, generations, (int)count, expectedBodies);
                byte[] nativeState = reader.ReadBytes(checked((int)(stream.Length - stream.Position)));
                return new PhysicsSnapshotData(snapshot.WorkerThreads, generations, entries, nativeState);
            }
            catch (EndOfStreamException error)
            {
                throw new ArgumentException("物理状态截断。", nameof(snapshot), error);
            }
        }

        private static BodyEntry[] ReadEntries(BinaryReader reader, byte[] generations, int count, IReadOnlyList<BodyState>? expected)
        {
            var entries = new BodyEntry[count];
            var slots = new HashSet<int>();
            ulong previous = 0;
            for (int index = 0; index < count; index++)
            {
                BodyDefinition definition = ReadDefinition(reader);
                uint bodyId = reader.ReadUInt32();
                int slot = BodyRegistry.Slot(bodyId);
                Require(definition.Id > previous && (bodyId & 0x80000000) == 0 && slot < BodyRegistry.Capacity
                    && slots.Add(slot) && generations[slot] == (byte)(bodyId >> 23), "物理身份或分配状态无效。");
                if (expected != null)
                {
                    Require(expected[index].Id == definition.Id && expected[index].Dynamic == definition.Dynamic,
                        "物理诊断对象与元数据不一致。");
                }
                entries[index] = new BodyEntry(definition, bodyId);
                previous = definition.Id;
            }
            return entries;
        }

        internal static PhysicsSnapshot Write(BodyRegistry bodies, int workers, byte[] nativeState)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic);
            writer.Write((uint)workers);
            writer.Write(bodies.CopyGenerations());
            writer.Write((uint)bodies.Count);
            foreach (BodyEntry entry in bodies.Entries)
            {
                WriteDefinition(writer, entry.Definition);
                writer.Write(entry.BodyId);
            }
            writer.Write(nativeState);
            return new PhysicsSnapshot(JoltWorldFactory.Identity, stream.ToArray(), workers);
        }

        private static BodyDefinition ReadDefinition(BinaryReader reader)
        {
            ulong entity = reader.ReadUInt64();
            int shape = reader.ReadInt32(), motion = reader.ReadInt32();
            Require(motion == 0 || motion == 1, "无效刚体运动类型。");
            Vector3 size = ReadVector(reader), position = ReadVector(reader);
            var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return new BodyDefinition(entity, (ShapeKind)shape, motion == 1, size, position, rotation,
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteDefinition(BinaryWriter writer, BodyDefinition definition)
        {
            writer.Write(definition.Id);
            writer.Write((int)definition.Shape);
            writer.Write(definition.Dynamic ? 1 : 0);
            WriteVector(writer, definition.Size);
            WriteVector(writer, definition.Position);
            writer.Write(definition.Rotation.X);
            writer.Write(definition.Rotation.Y);
            writer.Write(definition.Rotation.Z);
            writer.Write(definition.Rotation.W);
            writer.Write(definition.Mass);
            writer.Write(definition.Friction);
            writer.Write(definition.Restitution);
        }

        private static Vector3 ReadVector(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new ArgumentException(message);
            }
        }
    }
}
