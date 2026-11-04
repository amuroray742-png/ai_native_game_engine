using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using Xunit;

namespace AiNative.Tests
{
    public sealed class ManagedPhysicsTests
    {
        private static readonly JoltWorldFactory Factory = new JoltWorldFactory();
        private static BodyDefinition Ball(ulong id, float height = 0.5f) => new BodyDefinition(id,
            ShapeKind.Sphere, true, new Vector3(0.5f), new Vector3(0, height, 0), Quaternion.Identity);

        [Fact]
        public void EmptyWorldAndLastBodyRemovalRestoreAllocationState()
        {
            using IPhysicsWorld original = Factory.Create(2);
            using IPhysicsWorld empty = Factory.Restore(original.Capture());
            Assert.Empty(empty.GetBodies());
            original.Add(Ball(9));
            original.Remove(9);
            Assert.Throws<InvalidOperationException>(() => original.Capture());
            original.Step(1f / 60);
            using IPhysicsWorld restored = Factory.Restore(original.Capture());
            original.Add(Ball(ulong.MaxValue));
            restored.Add(Ball(ulong.MaxValue));
            Assert.Equal(original.Capture().GetData(), restored.Capture().GetData());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void EntityOrderingAndRetiredContactsSurviveDifferentBodyIdOrder(int workers)
        {
            using IPhysicsWorld world = Factory.Create(workers);
            const ulong ground = ulong.MaxValue - 1;
            world.Add(new BodyDefinition(ground, ShapeKind.Box, false, new Vector3(10, 0.5f, 10),
                new Vector3(0, -0.5f, 0), Quaternion.Identity));
            world.Add(Ball(7));
            PhysicsContact started = Assert.Single(world.Step(1f / 60));
            Assert.True(started.Started);
            Assert.Equal(7UL, started.First);
            Assert.Equal(ground, started.Second);
            Assert.Equal(new[] { 7UL, ground }, world.GetBodies().Select(body => body.Id));
            using IPhysicsWorld restored = Factory.Restore(world.Capture());
            foreach (IPhysicsWorld candidate in new[] { world, restored })
            {
                candidate.Remove(7);
                candidate.Add(Ball(ulong.MaxValue, 5));
            }
            var expected = world.Step(1f / 60);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(restored.Step(1f / 60)));
            Assert.Contains(expected, contact => !contact.Started && contact.First == 7 && contact.Second == ground);
            Assert.Equal(world.Capture().GetData(), restored.Capture().GetData());
        }

        [Fact]
        public void RejectedManagedChangesLeaveAllocationAndSnapshotsUnchanged()
        {
            using IPhysicsWorld world = Factory.Create();
            world.Add(Ball(42));
            PhysicsSnapshot snapshot = world.Capture();
            Assert.Throws<InvalidOperationException>(() => world.Add(Ball(42)));
            Assert.Throws<InvalidOperationException>(() => world.Remove(99));
            Assert.Throws<InvalidOperationException>(() => world.ApplyImpulse(99, Vector3.One));
            Assert.Equal(snapshot.GetData(), world.Capture().GetData());
            using IPhysicsWorld restored = Factory.Restore(snapshot);
            world.Add(Ball(1, 5));
            restored.Add(Ball(1, 5));
            Assert.Equal(world.Capture().GetData(), restored.Capture().GetData());
        }

        [Fact]
        public void GenerationWrapAcrossStepsRemainsReproducibleAfterRestore()
        {
            using IPhysicsWorld original = Factory.Create();
            original.Add(Ball(1));
            for (ulong id = 2; id <= 128; id++)
            {
                original.Remove(id - 1);
                original.Add(Ball(id));
                original.Step(1f / 60);
            }
            using IPhysicsWorld restored = Factory.Restore(original.Capture());
            for (ulong id = 129; id <= 270; id++)
            {
                original.Remove(id - 1);
                restored.Remove(id - 1);
                original.Add(Ball(id));
                restored.Add(Ball(id));
                Assert.Equal(JsonSerializer.Serialize(original.Step(1f / 60)), JsonSerializer.Serialize(restored.Step(1f / 60)));
                Assert.Equal(original.Capture().GetData(), restored.Capture().GetData());
            }
        }

        [Fact]
        public void OldBackendAndSnapshotFormatAreRejectedBeforeCandidateAllocation()
        {
            using IPhysicsWorld world = Factory.Create();
            byte[] bytes = world.Capture().GetData();
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            Assert.Throws<ArgumentException>(() => Factory.Restore(new PhysicsSnapshot(Factory.Backend.Replace("abi=4", "abi=3"), bytes)));
            bytes[3] = 0x33;
            Assert.Throws<ArgumentException>(() => Factory.Restore(new PhysicsSnapshot(Factory.Backend, bytes)));
            Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
        }

        [Fact]
        public void FullSnapshotTruncationsAndMutationsCleanCandidatesAndPreserveOldWorld()
        {
            using IPhysicsWorld original = Factory.Create();
            original.Add(new BodyDefinition(900, ShapeKind.Box, false, new Vector3(10, 0.5f, 10),
                new Vector3(0, -0.5f, 0), Quaternion.Identity));
            original.Add(Ball(7));
            for (int tick = 0; tick < 30; tick++)
            {
                original.Step(1f / 60);
            }
            byte[] saved = original.Capture().GetData();
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            for (int length = 1; length < saved.Length; length++)
            {
                Exception? error = Record.Exception(() =>
                {
                    using IPhysicsWorld candidate = Factory.Restore(new PhysicsSnapshot(Factory.Backend, saved.Take(length).ToArray()));
                });
                Assert.True(error is ArgumentException || error is InvalidOperationException, $"Truncation {length}: {error}");
                Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
            }
            for (int offset = 0; offset < saved.Length; offset++)
            {
                byte[] changed = (byte[])saved.Clone();
                changed[offset] ^= 0xff;
                Exception? error = Record.Exception(() =>
                {
                    using IPhysicsWorld candidate = Factory.Restore(new PhysicsSnapshot(Factory.Backend, changed));
                    Assert.Equal(changed, candidate.Capture().GetData());
                });
                Assert.True(error == null || error is ArgumentException || error is InvalidOperationException, $"Mutation {offset}: {error}");
                Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
            }
            Assert.Equal(saved, original.Capture().GetData());
            original.Step(1f / 60);
        }
    }
}
