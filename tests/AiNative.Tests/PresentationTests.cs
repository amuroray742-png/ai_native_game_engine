using System;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using Xunit;

namespace AiNative.Tests
{
    public sealed class PresentationTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FastOrDelayedPlayback_UsesEachStepsOwnStateAndDoesNotDriveSimulation(bool immediate)
        {
            GridGameRuntime game = GridGameRuntime.Create(Fixtures.Definition(), 42);
            var presentation = new GridPresentationQueue(game.Capture());
            for (int index = 0; index < 2; index++)
            {
                game.Submit(new Move(Side.A, 1, MoveDirection.Up));
                GridStep step = Assert.IsType<GridStep>(game.Advance());
                presentation.Enqueue(step);
                if (immediate)
                {
                    Assert.Same(step, presentation.BeginNext());
                    Assert.True(presentation.Complete(step));
                }
            }
            GridObservation final = game.Capture();
            Assert.Equal(new GridPosition(0, 2), final.Game.Units[0].Position);
            if (!immediate)
            {
                Assert.False(presentation.IsSettledAt(final));
                Assert.Equal(new GridPosition(0, 0), presentation.Presented.Game.Units[0].Position);
                GridStep first = Assert.IsType<GridStep>(presentation.BeginNext());
                Assert.Equal(new GridPosition(0, 1), first.State.Game.Units[0].Position);
                Assert.Throws<InvalidOperationException>(() => presentation.BeginNext());
                Assert.True(presentation.Complete(first));
                Assert.False(presentation.Complete(first));
                GridStep second = Assert.IsType<GridStep>(presentation.BeginNext());
                Assert.True(presentation.Complete(second));
            }
            Assert.True(presentation.IsSettledAt(final));
            Assert.Null(presentation.BeginNext());
            Assert.Equal(2, game.CompletedStepCount);
            Assert.Equal(42UL, game.RandomState);
            Assert.Equal(0, game.GetState().ActionPoints);
        }

        [Fact]
        public void ResetDuringAnimation_InvalidatesOldCallbacksAndForeignSteps()
        {
            GridGameRuntime original = GridGameRuntime.Create(Fixtures.Definition(), 42);
            var presentation = new GridPresentationQueue(original.Capture());
            original.Submit(new Move(Side.A, 1, MoveDirection.Up));
            GridStep oldStep = Assert.IsType<GridStep>(original.Advance());
            presentation.Enqueue(oldStep);
            Assert.Same(oldStep, presentation.BeginNext());
            Assert.True(presentation.IsActive(oldStep));
            GridGameRuntime restored = GridGameRuntime.Restore(original.ExportState());
            presentation.Reset(restored.Capture());
            GridObservation rendered = restored.Capture();
            bool staleCallbackWroteView = false;
            if (presentation.IsActive(oldStep))
            {
                staleCallbackWroteView = true;
                rendered = oldStep.State;
                presentation.Complete(oldStep);
            }
            Assert.False(staleCallbackWroteView);
            Assert.True(rendered.IsFromSameGame(restored.Capture()));
            Assert.False(presentation.IsActive(oldStep));
            Assert.False(presentation.Complete(oldStep));
            original.Submit(new EndTurn(Side.A));
            GridStep stale = Assert.IsType<GridStep>(original.Advance());
            Assert.Throws<InvalidOperationException>(() => presentation.Enqueue(stale));
            restored.Submit(new EndTurn(Side.A));
            GridStep current = Assert.IsType<GridStep>(restored.Advance());
            presentation.Enqueue(current);
            Assert.Same(current, presentation.BeginNext());
            Assert.True(presentation.IsActive(current));
            Assert.True(presentation.Complete(current));
            Assert.False(presentation.IsActive(current));
            Assert.True(presentation.IsSettledAt(restored.Capture()));
            Assert.False(presentation.IsSettledAt(original.Capture()));
        }

        [Fact]
        public void Queue_RejectsMissingDuplicateStepsAndPendingSimulationScreenshot()
        {
            GridGameRuntime game = GridGameRuntime.Create(Fixtures.Definition(), 42);
            var presentation = new GridPresentationQueue(game.Capture());
            game.Submit(new Move(Side.A, 1, MoveDirection.Left));
            GridStep rejected = Assert.IsType<GridStep>(game.Advance());
            Assert.Equal("OutOfBounds", rejected.Step.Result.Error);
            presentation.Enqueue(rejected);
            Assert.Throws<InvalidOperationException>(() => presentation.Enqueue(rejected));
            Assert.Same(rejected, presentation.BeginNext());
            Assert.True(presentation.Complete(rejected));
            game.Submit(new Move(Side.A, 1, MoveDirection.Up));
            Assert.False(presentation.IsSettledAt(game.Capture()));
            game.Advance();
            game.Submit(new Move(Side.A, 1, MoveDirection.Up));
            GridStep skipped = Assert.IsType<GridStep>(game.Advance());
            Assert.Throws<InvalidOperationException>(() => presentation.Enqueue(skipped));
        }
    }
}
