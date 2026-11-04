using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using AiNative.PhysicsSandbox;
using Xunit;

namespace AiNative.Tests
{
    public sealed class PhysicsTests
    {
        private static readonly JoltWorldFactory Factory = new JoltWorldFactory();
        private static BodyDefinition Ground => new BodyDefinition(1, ShapeKind.Box, false,
            new Vector3(10, 0.5f, 10), new Vector3(0, -0.5f, 0), Quaternion.Identity);
        private static BodyDefinition Ball(ulong id = 2) => new BodyDefinition(id, ShapeKind.Sphere, true,
            new Vector3(0.5f), new Vector3(0, 3, 0), Quaternion.Identity);

        [Fact]
        public void FreeFallContactsSleepAndSeparationUseRealJolt()
        {
            using IPhysicsWorld world = Factory.Create();
            world.Add(Ground);
            world.Add(Ball());
            world.Step(1f / 60);
            BodyState falling = world.GetBodies()[1];
            Assert.True(falling.Position.Y < 3);
            Assert.True(falling.Velocity.Y < 0);
            var contacts = new List<PhysicsContact>();
            for (int tick = 0; tick < 300; tick++)
            {
                contacts.AddRange(world.Step(1f / 60));
            }
            PhysicsContact started = Assert.Single(contacts, contact => contact.Started);
            Assert.Equal(1UL, started.First);
            Assert.Equal(2UL, started.Second);
            Assert.False(world.GetBodies()[1].Active);
            Assert.InRange(world.GetBodies()[1].Position.Y, 0.45f, 0.55f);
            world.ApplyImpulse(2, new Vector3(0, 8, 0));
            for (int tick = 0; tick < 10; tick++)
            {
                contacts.AddRange(world.Step(1f / 60));
            }
            Assert.Contains(contacts, contact => !contact.Started && contact.First == 1 && contact.Second == 2);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(20)]
        [InlineData(60)]
        [InlineData(300)]
        public void CompleteStateRestoresFutureBodiesContactsAndAllocator(int boundary)
        {
            using IPhysicsWorld continuous = Factory.Create();
            continuous.Add(Ground);
            continuous.Add(Ball());
            for (int tick = 0; tick < boundary; tick++)
            {
                continuous.Step(1f / 60);
            }
            PhysicsSnapshot saved = continuous.Capture();
            using IPhysicsWorld restored = Factory.Restore(saved);
            Assert.Equal(saved.GetData(), restored.Capture().GetData());
            for (int tick = 0; tick < 180; tick++)
            {
                if (tick == 2)
                {
                    continuous.Remove(2);
                    restored.Remove(2);
                    continuous.Add(Ball(3));
                    restored.Add(Ball(3));
                }
                if (tick == 90)
                {
                    continuous.ApplyImpulse(3, new Vector3(2, 5, 0));
                    restored.ApplyImpulse(3, new Vector3(2, 5, 0));
                }
                Assert.Equal(JsonSerializer.Serialize(continuous.Step(1f / 60)), JsonSerializer.Serialize(restored.Step(1f / 60)));
                Assert.Equal(JsonSerializer.Serialize(continuous.GetBodies()), JsonSerializer.Serialize(restored.GetBodies()));
                Assert.Equal(continuous.Capture().GetData(), restored.Capture().GetData());
            }
            using IPhysicsWorld restoredAgain = Factory.Restore(restored.Capture());
            Assert.Equal(restored.Capture().GetData(), restoredAgain.Capture().GetData());
        }

        [Fact]
        public void RejectedInputDoesNotPreventLaterInputsOrPhysics()
        {
            using var runtime = SimulationRuntime<SandboxCommand, IReadOnlyList<BodyState>, SandboxState>.Create(
                () => SandboxSimulation.Create(Factory, new[] { Ground, Ball() }), 42, new SimulationTiming(60));
            runtime.Submit(new ApplyImpulse(999, Vector3.One));
            runtime.Submit(new ApplyImpulse(1, Vector3.One));
            runtime.Submit(new SpawnBody(ShapeKind.Box, new Vector3(0.5f), new Vector3(5, 5, 0)));
            TickResult<IReadOnlyList<BodyState>> result = runtime.Advance();
            Assert.Equal(new[] { "UnknownBody", "StaticBody", null }, result.Inputs.Select(input => input.Result.Error));
            Assert.Equal(3, result.Observation.State.Count);
            Assert.True(result.Observation.State[1].Position.Y < 3);
            Assert.Equal(42UL, runtime.RandomState);
            Assert.Equal(1, runtime.Tick);
            Assert.Equal(3, runtime.Processed);
            runtime.Submit(new RemoveBody(3));
            Assert.Throws<InvalidOperationException>(() => runtime.ExportState());
        }

        [Fact]
        public void WorldsAreIsolatedAndDisposalReturnsResourcesToBaseline()
        {
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            for (int iteration = 0; iteration < 20; iteration++)
            {
                IPhysicsWorld first = Factory.Create();
                IPhysicsWorld second = Factory.Create();
                first.Add(Ball());
                second.Add(Ball());
                first.ApplyImpulse(2, Vector3.UnitX);
                first.Step(1f / 60);
                Assert.Equal(new Vector3(0, 3, 0), second.GetBodies()[0].Position);
                Assert.Equal(baseline + 2, JoltWorldFactory.ActiveWorldCount);
                first.Dispose();
                first.Dispose();
                second.Dispose();
                Assert.Throws<ObjectDisposedException>(() => first.Capture());
                Assert.Throws<ObjectDisposedException>(() => first.Step(1f / 60));
                Assert.Throws<ObjectDisposedException>(() => first.Add(Ball()));
                Assert.Throws<ObjectDisposedException>(() => first.Remove(2));
                Assert.Throws<ObjectDisposedException>(() => first.ApplyImpulse(2, Vector3.One));
                Assert.Throws<ObjectDisposedException>(() => first.GetBodies());
                Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
            }
        }

        [Fact]
        public void FailedCandidateAndInvalidNativeArgumentsLeaveOldWorldUsable()
        {
            using IPhysicsWorld original = Factory.Create();
            original.Add(Ball());
            PhysicsSnapshot saved = original.Capture();
            byte[] bytes = saved.GetData();
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            Assert.Throws<ArgumentException>(() => Factory.Restore(new PhysicsSnapshot("wrong-native-version", bytes)));
            // Truncate inside Jolt's stream, after valid bridge metadata, to exercise candidate cleanup.
            Assert.Throws<InvalidOperationException>(() => Factory.Restore(new PhysicsSnapshot(Factory.Backend, bytes.Take(1110).ToArray())));
            Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
            Assert.Throws<InvalidOperationException>(() => original.Add(Ball()));
            Assert.Throws<InvalidOperationException>(() => original.Remove(999));
            Assert.Throws<InvalidOperationException>(() => original.ApplyImpulse(999, Vector3.One));
            Assert.Throws<InvalidOperationException>(() => original.Step(float.NaN));
            Assert.Equal(saved.GetData(), original.Capture().GetData());
            original.Step(1f / 60);
            Assert.True(original.GetBodies()[0].Position.Y < 3);
        }

        [Fact]
        public void SnapshotBytesAreIsolatedAndManagedValidationRejectsBadMetadata()
        {
            using IPhysicsWorld world = Factory.Create();
            world.Add(Ball());
            PhysicsSnapshot saved = world.Capture();
            byte[] changed = saved.GetData();
            changed[0] = 0;
            Assert.NotEqual(changed, saved.GetData());
            Assert.Throws<ArgumentException>(() => Factory.ValidateSnapshot(new PhysicsSnapshot(Factory.Backend, changed)));
            Assert.Throws<ArgumentException>(() => Factory.ValidateSnapshot(new PhysicsSnapshot(Factory.Backend, new byte[1033])));
            byte[] source = saved.GetData();
            var isolated = new PhysicsSnapshot(Factory.Backend, source);
            source[0] = 0;
            Assert.Equal(saved.GetData(), isolated.GetData());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void InvalidBodyDefinitionsAreRejectedBeforeNativeAllocation(int variant)
        {
            Assert.Throws<ArgumentException>(() => new BodyDefinition(variant == 0 ? 0UL : 1UL,
                variant == 1 ? (ShapeKind)99 : ShapeKind.Box, true,
                variant == 2 ? Vector3.Zero : Vector3.One,
                variant == 3 ? new Vector3(float.NaN) : Vector3.Zero,
                variant == 4 ? new Quaternion() : Quaternion.Identity, restitution: variant == 5 ? 2 : 0));
        }

        [Fact]
        public void ManyChangesInOneTickCannotReuseAnUndeliveredContactIdentity()
        {
            using IPhysicsWorld world = Factory.Create();
            world.Add(Ground);
            world.Add(new BodyDefinition(2, ShapeKind.Sphere, true, new Vector3(0.5f), new Vector3(0, 0.5f, 0), Quaternion.Identity));
            Assert.Contains(world.Step(1f / 60), contact => contact.Started && contact.Second == 2);
            ulong current = 2;
            for (ulong next = 3; next <= 260; next++)
            {
                world.Remove(current);
                if (next == 3)
                {
                    Assert.Throws<InvalidOperationException>(() => world.Capture());
                }
                world.Add(Ball(next));
                current = next;
            }
            PhysicsContact ended = Assert.Single(world.Step(1f / 60), contact => !contact.Started);
            Assert.Equal(2UL, ended.Second);
            using IPhysicsWorld restored = Factory.Restore(world.Capture());
            Assert.Equal(world.Capture().GetData(), restored.Capture().GetData());
        }

        [Fact]
        public void OfflineStateValidationRejectsDiagnosticsThatDisagreeWithBodyMetadata()
        {
            RecordedRun run = Fixtures.Record("physics-sandbox", commands: Fixtures.Read("empty.commands.json"));
            foreach (bool changeIdentity in new[] { true, false })
            {
                JsonElement damaged = HostingTests.Change(run.Snapshot, node =>
                {
                    if (changeIdentity)
                    { node["state"]!["bodies"]![0]!["id"] = "9"; node["state"]!["nextId"] = "10"; }
                    else
                    { node["state"]!["bodies"]![0]!["dynamic"] = true; }
                    HostingTests.Rehash(node, run.Snapshot);
                });
                Assert.Throws<ProtocolException>(() => StateCodec.Read(damaged, Fixtures.Registry()));
            }
        }

        [Fact]
        public void SharedToolchainRecordsAndReexecutesPhysicalState()
        {
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            RecordedRun run = Fixtures.Record("physics-sandbox");
            ReplayRecord record = ReplayRecord.Read(run.Replay, Fixtures.Registry());
            using (IHostedGame restored = StateCodec.Restore(record.Initial, Fixtures.Environment()))
            {
                RecordedRun replayed = HostVerification.Record(record.Initial.Module, record.Initial.Configuration,
                    restored, record.Commands, Fixtures.Environment(), record);
                Assert.Equal(JsonData.Hash(run.Snapshot), JsonData.Hash(replayed.Snapshot));
            }
            Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
        }
    }
}
