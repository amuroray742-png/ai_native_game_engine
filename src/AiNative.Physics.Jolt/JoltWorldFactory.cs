using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AiNative.Physics.Jolt
{
    public sealed class JoltWorldFactory : IPhysicsWorldFactory
    {
        public const string Identity = "jolt-5.6.0;e77f175;joltc=2bbcf73;abi=4;float32;workers=0..16;deterministic;sse4;capacity=1024";
        public string Backend => Identity;
        /// <summary>进程累计的工作线程碰撞回调数，仅用于诊断，不进入模拟状态。</summary>
        public static ulong WorkerContactCount => JoltNative.aj_worker_contact_count();
        public static uint ActiveWorldCount => JoltNative.aj_world_count();

        public IPhysicsWorld Create(int workerThreads = 0) => CreateCandidate(workerThreads, new BodyRegistry());

        public IPhysicsWorld Restore(PhysicsSnapshot snapshot)
        {
            PhysicsSnapshotData data = PhysicsSnapshotCodec.Read(snapshot);
            JoltWorld candidate = CreateCandidate(data.Workers, new BodyRegistry(data.Generations));
            try
            {
                foreach (BodyEntry entry in data.Entries)
                {
                    candidate.AddRestored(entry);
                }
                candidate.RestoreInternalState(data.NativeState);
                if (!candidate.Capture().GetData().SequenceEqual(snapshot.GetData()))
                {
                    throw new InvalidOperationException("Restored physics state does not match the snapshot.");
                }
                return candidate;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        /// <summary>仅检查托管元数据，不加载原生库；恢复时才验证 Jolt 内部状态。</summary>
        public void ValidateSnapshot(PhysicsSnapshot snapshot, IReadOnlyList<BodyState>? expectedBodies = null)
        {
            PhysicsSnapshotCodec.Read(snapshot, expectedBodies);
        }

        private static JoltWorld CreateCandidate(int workers, BodyRegistry bodies)
        {
            if (workers < 0 || workers > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(workers));
            }
            string? actual = Marshal.PtrToStringAnsi(JoltNative.aj_version());
            if (actual != Identity)
            {
                throw new BadImageFormatException("NativeLibraryVersionMismatch: expected " + Identity + "; actual " + actual);
            }
            JoltNative.Check(JoltNative.aj_create(workers, out WorldHandle handle));
            try
            {
                return new JoltWorld(handle, workers, bodies);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }
}
