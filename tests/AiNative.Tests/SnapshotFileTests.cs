using System;
using System.IO;
using System.Text;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class SnapshotFileTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void SaveLoad_EveryDemoBoundary_ContinuesWithMatchingResults(int boundary)
        {
            using var directory = new SnapshotDirectory();
            string scenarioPath = Path.Combine(directory.Path, "scenario.json");
            File.WriteAllBytes(scenarioPath, SnapshotFixtures.Read("scenario.json"));
            ScenarioDefinition configuration = ScenarioLoader.Load(scenarioPath);
            File.Delete(scenarioPath);
            GridGameRuntime original = GridGameRuntime.Create(configuration, 42);
            GridCommand[] commands =
            {
                new Move(Side.A, 1, MoveDirection.Right), new Attack(Side.A, 1, 2), new EndTurn(Side.A),
                new Attack(Side.B, 2, 1), new EndTurn(Side.B), new Attack(Side.A, 1, 2)
            };
            for (int index = 0; index < boundary; index++)
            {
                original.Submit(commands[index]);
                original.Advance();
            }
            var store = new SnapshotStore();
            string before = Fixtures.State(original.GetState());
            Assert.Equal(directory.FilePath, store.Save(directory.FilePath, original));
            Assert.Equal(before, Fixtures.State(original.GetState()));
            Assert.Equal(boundary, original.CompletedStepCount);
            GridGameRuntime restored = new SnapshotStore().Load(directory.FilePath);
            Assert.Equal(before, Fixtures.State(restored.GetState()));
            Assert.Equal(boundary, restored.CompletedStepCount);
            for (int index = boundary; index < commands.Length; index++)
            {
                original.Submit(commands[index]);
                restored.Submit(commands[index]);
                StepResult expected = Assert.IsType<GridStep>(original.Advance()).Step;
                StepResult actual = Assert.IsType<GridStep>(restored.Advance()).Step;
                Assert.Equal(Fixtures.Trace(new DemoStep("command", expected, original.GetState())),
                    Fixtures.Trace(new DemoStep("command", actual, restored.GetState())));
                Assert.Equal(original.RandomState, restored.RandomState);
            }
            Assert.Equal(Side.A, restored.GetState().Winner);
            Assert.Equal(2, restored.GetState().Units[0].Health);
            Assert.Equal(0, restored.GetState().Units[1].Health);
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public void Save_NonemptyOrFaultedSession_DoesNotPublishOrChangeQueue()
        {
            using var directory = new SnapshotDirectory();
            GridGameRuntime session = GridGameRuntime.Create(Fixtures.Definition(), 42);
            session.Submit(new EndTurn(Side.A));
            var store = new SnapshotStore();
            Assert.Throws<InvalidOperationException>(() => store.Save(directory.FilePath, session));
            Assert.Equal(1, session.PendingCount);
            Assert.Equal(0, session.CompletedStepCount);
            Assert.Empty(Directory.GetFiles(directory.Path));

            ScenarioDefinition config = Fixtures.Definition();
            session = GridGameRuntime.Restore(new GridGameSnapshot(
                new SessionState(long.MaxValue - 1, long.MaxValue - 1, DeterministicRandom.Algorithm, 42),
                new GridGameState(config, config.Units, Side.A, 2, long.MaxValue, null)));
            session.Submit(new EndTurn(Side.A));
            Assert.Throws<OverflowException>(() => session.Advance());
            Assert.True(session.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => store.Save(directory.FilePath, session));
            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public void Load_FixtureEnvironmentMismatchAndMissingFile_AreDistinct()
        {
            using var directory = new SnapshotDirectory();
            var store = new SnapshotStore();
            HostOperationException missing = Assert.Throws<HostOperationException>(() => store.Load(directory.FilePath));
            Assert.Equal("InputError", missing.Category);
            Assert.Equal("InputReadFailed", missing.Code);
            File.WriteAllBytes(directory.FilePath, SnapshotFixtures.Read("initial.snapshot.json"));
            Assert.Equal("EnvironmentMismatch", Assert.Throws<ProtocolException>(() => store.Load(directory.FilePath)).Code);
        }

        [Fact]
        public void Publish_ExistingTarget_IsPreservedAndWriterIsNotCalled()
        {
            using var directory = new SnapshotDirectory();
            File.WriteAllText(directory.FilePath, "original");
            bool called = false;
            HostOperationException error = Assert.Throws<HostOperationException>(() => AtomicFilePublisher.Publish(directory.FilePath,
                stream => called = true));
            Assert.Equal("HostFailure", error.Category);
            Assert.Equal("OutputExists", error.Code);
            Assert.False(called);
            Assert.Equal("original", File.ReadAllText(directory.FilePath));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public void Publish_PartialWriteFailure_LeavesNoDestinationOrTemporaryFile()
        {
            using var directory = new SnapshotDirectory();
            HostOperationException error = Assert.Throws<HostOperationException>(() => AtomicFilePublisher.Publish(directory.FilePath, stream =>
            {
                stream.Write(Encoding.UTF8.GetBytes("partial"));
                throw new IOException("Injected write failure.");
            }));
            Assert.Equal("HostFailure", error.Category);
            Assert.Equal("OutputWriteFailed", error.Code);
            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public void Publish_TargetCreatedDuringWrite_IsNotOverwritten()
        {
            using var directory = new SnapshotDirectory();
            HostOperationException error = Assert.Throws<HostOperationException>(() => AtomicFilePublisher.Publish(directory.FilePath, stream =>
            {
                stream.Write(Encoding.UTF8.GetBytes("new snapshot"));
                Assert.False(File.Exists(directory.FilePath));
                File.WriteAllText(directory.FilePath, "concurrent owner");
            }));
            Assert.Equal("OutputExists", error.Code);
            Assert.Equal("concurrent owner", File.ReadAllText(directory.FilePath));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public void Publish_MissingParent_IsReportedWithoutCreatingDirectories()
        {
            using var directory = new SnapshotDirectory();
            string path = Path.Combine(directory.Path, "missing", "snapshot.json");
            HostOperationException error = Assert.Throws<HostOperationException>(() => AtomicFilePublisher.Publish(path, stream => stream.WriteByte(1)));
            Assert.Equal("OutputWriteFailed", error.Code);
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }

        [Fact]
        public void Save_OutputFailure_LeavesSimulationUnchanged()
        {
            using var directory = new SnapshotDirectory();
            GridGameRuntime session = GridGameRuntime.Create(Fixtures.Definition(), 42);
            session.Submit(new Move(Side.A, 1, MoveDirection.Up));
            session.Advance();
            string before = Fixtures.State(session.GetState());
            var store = new SnapshotStore();
            Assert.Throws<HostOperationException>(() => store.Save(Path.Combine(directory.Path, "missing", "state.json"), session));
            Assert.Equal(before, Fixtures.State(session.GetState()));
            Assert.Equal(42UL, session.RandomState);
            Assert.Equal(1, session.CompletedStepCount);
            Assert.False(session.IsFaulted);
            Assert.Equal(2, session.Submit(new EndTurn(Side.A)));
        }
    }
}
