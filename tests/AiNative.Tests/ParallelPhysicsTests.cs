using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using AiNative.Hosting;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using Xunit;

namespace AiNative.Tests
{
    public sealed class ParallelPhysicsTests
    {
        private static readonly JoltWorldFactory Factory = new JoltWorldFactory();

        private static IPhysicsWorld CreateScene(int workers)
        {
            IPhysicsWorld world = Factory.Create(workers);
            world.Add(new BodyDefinition(1, ShapeKind.Box, false, new Vector3(20, 0.5f, 20),
                new Vector3(0, -0.5f, 0), Quaternion.Identity));
            for (int index = 0; index < 128; index++)
            {
                world.Add(new BodyDefinition((ulong)index + 2, index % 2 == 0 ? ShapeKind.Box : ShapeKind.Sphere,
                    true, new Vector3(0.45f), new Vector3(index % 8 - 4, 0.5f + index / 64 * 1.1f, index / 8 % 8 - 4),
                    Quaternion.Identity));
            }
            return world;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void RepeatedAndRestoredWorldsMatchEveryTickUnderContactLoad(int workers)
        {
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            ulong callbacks = JoltWorldFactory.WorkerContactCount;
            using (IPhysicsWorld first = CreateScene(workers))
            using (IPhysicsWorld repeated = CreateScene(workers))
            {
                for (int tick = 0; tick < 90; tick++)
                {
                    Assert.Equal(JsonSerializer.Serialize(first.Step(1f / 60)), JsonSerializer.Serialize(repeated.Step(1f / 60)));
                    Assert.Equal(first.Capture().GetData(), repeated.Capture().GetData());
                }
                PhysicsSnapshot saved = first.Capture();
                Assert.Equal(workers, saved.WorkerThreads);
                using IPhysicsWorld restored = Factory.Restore(saved);
                Assert.Equal(saved.GetData(), restored.Capture().GetData());
                for (int tick = 0; tick < 240; tick++)
                {
                    if (tick == 5)
                    {
                        first.Remove(2);
                        restored.Remove(2);
                        var replacement = new BodyDefinition(200, ShapeKind.Sphere, true, new Vector3(0.45f),
                            new Vector3(0, 5, 0), Quaternion.Identity);
                        first.Add(replacement);
                        restored.Add(replacement);
                    }
                    if (tick == 40)
                    {
                        first.ApplyImpulse(3, new Vector3(2, 6, 0));
                        restored.ApplyImpulse(3, new Vector3(2, 6, 0));
                    }
                    Assert.Equal(JsonSerializer.Serialize(first.Step(1f / 60)), JsonSerializer.Serialize(restored.Step(1f / 60)));
                    Assert.Equal(first.Capture().GetData(), restored.Capture().GetData());
                }
                Assert.Contains(first.GetBodies(), body => body.Dynamic && !body.Active);
            }
            Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
            if (workers == 0)
            {
                Assert.Equal(callbacks, JoltWorldFactory.WorkerContactCount);
            }
            else
            {
                Assert.True(JoltWorldFactory.WorkerContactCount > callbacks, "Real worker threads must execute collision callbacks.");
            }
        }

        [Fact]
        public async Task SeparateWorldsCanUpdateConcurrentlyWithoutSharingTemporaryState()
        {
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            byte[] Run()
            {
                using IPhysicsWorld world = CreateScene(2);
                for (int tick = 0; tick < 180; tick++)
                {
                    world.Step(1f / 60);
                }
                return world.Capture().GetData();
            }
            byte[] expected = Run();
            byte[][] actual = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Run)));
            Assert.All(actual, snapshot => Assert.Equal(expected, snapshot));
            Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
        }

        [Fact]
        public void WorkerConfigurationIsValidatedBeforeAllocationAndRecordedInSnapshots()
        {
            uint baseline = JoltWorldFactory.ActiveWorldCount;
            Assert.Throws<ArgumentOutOfRangeException>(() => Factory.Create(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Factory.Create(17));
            using (IPhysicsWorld world = CreateScene(2))
            {
                PhysicsSnapshot saved = world.Capture();
                Assert.Throws<ArgumentException>(() => Factory.Restore(new PhysicsSnapshot(saved.Backend, saved.GetData(), 0)));
                byte[] damaged = saved.GetData();
                damaged[4] = 3;
                Assert.Throws<ArgumentException>(() => Factory.Restore(new PhysicsSnapshot(saved.Backend, damaged, 2)));
                Assert.Equal(saved.GetData(), world.Capture().GetData());
            }
            Assert.Equal(baseline, JoltWorldFactory.ActiveWorldCount);
        }

        [Fact]
        public void HostRejectsSnapshotWhoseWorkerCountDiffersFromConfiguration()
        {
            RecordedRun run = Fixtures.Record("physics-sandbox", commands: Fixtures.Read("empty.commands.json"));
            JsonElement damaged = HostingTests.Change(run.Snapshot, node =>
            {
                node["state"]!["physics"]!["workerThreads"] = 0;
                HostingTests.Rehash(node, run.Snapshot);
            });
            Assert.Equal("PhysicsSettingsMismatch", Assert.Throws<ProtocolException>(
                () => StateCodec.Read(damaged, Fixtures.Registry())).Code);
        }
    }
}
