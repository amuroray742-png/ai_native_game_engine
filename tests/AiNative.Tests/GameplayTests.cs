using System;
using System.Collections.Generic;
using System.Linq;
using AiNative.Core;
using AiNative.GridGame;
using Xunit;

namespace AiNative.Tests
{
    public sealed class GameplayTests
    {
        [Fact]
        public void Move_WhenValid_ChangesOnlyPositionAndActionPoints()
        {
            var session = Fixtures.Session();
            GridGameView before = session.GetState();
            CommandResult result = Fixtures.Execute(session, new Move(Side.A, 1, new GridPosition(1, 0)));
            UnitMoved moved = Assert.IsType<UnitMoved>(Assert.Single(result.Events));
            GridGameView after = session.GetState();
            Assert.True(result.Succeeded);
            Assert.Equal(new GridPosition(0, 0), moved.From);
            Assert.Equal(new GridPosition(1, 0), moved.To);
            Assert.Equal(moved.To, after.Units[0].Position);
            Assert.Equal(1, after.ActionPoints);
            Assert.Equal(before.TurnIndex, after.TurnIndex);
            Assert.Equal(before.ActiveSide, after.ActiveSide);
            Assert.Equal(before.Units[0].Health, after.Units[0].Health);
            Assert.Equal(before.Units[1], after.Units[1]);
            Assert.Equal(42UL, session.RandomState);
            Assert.Equal(new GridPosition(0, 0), before.Units[0].Position);
        }

        [Theory]
        [InlineData(-1, 0, "OutOfBounds")]
        [InlineData(5, 0, "OutOfBounds")]
        [InlineData(0, 5, "OutOfBounds")]
        [InlineData(2, 2, "Blocked")]
        [InlineData(2, 0, "Occupied")]
        [InlineData(0, 0, "Occupied")]
        [InlineData(1, 1, "NotAdjacent")]
        [InlineData(0, 2, "NotAdjacent")]
        [InlineData(int.MaxValue, int.MinValue, "OutOfBounds")]
        public void Move_WhenDestinationInvalid_LeavesStateUnchanged(int x, int y, string error)
        {
            Fixtures.AssertRejected(Fixtures.Session(), new Move(Side.A, 1, new GridPosition(x, y)), error);
        }

        [Fact]
        public void Commands_WhenSideOrEntityInvalid_LeaveStateUnchanged()
        {
            var session = Fixtures.Session();
            Fixtures.AssertRejected(session, new Move(Side.B, 2, new GridPosition(3, 0)), "NotYourTurn");
            Fixtures.AssertRejected(session, new Move(Side.A, 2, new GridPosition(3, 0)), "NotYourUnit");
            Fixtures.AssertRejected(session, new Move(Side.A, 99, new GridPosition(1, 0)), "UnknownUnit");
            Fixtures.AssertRejected(session, new EndTurn(Side.B), "NotYourTurn");
            Fixtures.AssertRejected(session, new EndTurn((Side)999), "NotYourTurn");
        }

        [Fact]
        public void ActionPoints_WhenExhausted_RequireExplicitEndTurn()
        {
            var session = Fixtures.Session();
            Fixtures.Execute(session, new Move(Side.A, 1, new GridPosition(1, 0)));
            Fixtures.Execute(session, new Attack(Side.A, 1, 2));
            Assert.Equal(0, session.GetState().ActionPoints);
            Assert.Equal(Side.A, session.GetState().ActiveSide);
            Fixtures.AssertRejected(session, new Move(Side.A, 1, new GridPosition(1, 1)), "NoActionPoints");
            Fixtures.AssertRejected(session, new Attack(Side.A, 1, 2), "NoActionPoints");
            Fixtures.AssertRejected(session, new EndTurn(Side.B), "NotYourTurn");
            CommandResult ended = Fixtures.Execute(session, new EndTurn(Side.A));
            TurnChanged turn = Assert.IsType<TurnChanged>(Assert.Single(ended.Events));
            Assert.Equal(Side.B, turn.Side);
            Assert.Equal(2, turn.TurnIndex);
            Assert.Equal(2, session.GetState().ActionPoints);
            Assert.Equal(Side.B, session.GetState().ActiveSide);
        }

        [Fact]
        public void QueuedCommands_AreValidatedAgainstExecutionTimeState()
        {
            var session = Fixtures.Session();
            session.Submit(new EndTurn(Side.A));
            session.Submit(new Move(Side.A, 1, new GridPosition(1, 0)));
            session.Submit(new Move(Side.B, 2, new GridPosition(3, 0)));
            Assert.True(Assert.IsType<StepResult>(session.Advance()).Result.Succeeded);
            Assert.Equal("NotYourTurn", Assert.IsType<StepResult>(session.Advance()).Result.Error);
            Assert.True(Assert.IsType<StepResult>(session.Advance()).Result.Succeeded);
            Assert.Equal(new GridPosition(3, 0), session.GetState().Units[1].Position);
        }

        [Fact]
        public void Attack_WhenInvalid_LeavesStateUnchanged()
        {
            var session = Fixtures.Session();
            Fixtures.AssertRejected(session, new Attack(Side.A, 1, 1), "FriendlyTarget");
            Fixtures.AssertRejected(session, new Attack(Side.A, 1, 99), "UnknownTarget");
            Fixtures.AssertRejected(session, new Attack(Side.A, 99, 2), "UnknownUnit");
            Fixtures.AssertRejected(session, new Attack(Side.A, 2, 1), "NotYourUnit");
            Fixtures.AssertRejected(session, new Attack(Side.B, 2, 1), "NotYourTurn");
            Fixtures.AssertRejected(session, new Attack(Side.A, 1, 2), "NotAdjacent");
        }

        [Fact]
        public void Attack_WhenLethal_ClampsHealthAndRejectsRemainingQueue()
        {
            ScenarioDefinition source = Fixtures.Definition();
            var definition = new ScenarioDefinition(source.Width, source.Height, source.Obstacles, source.Units, 2, int.MaxValue);
            var session = new SimulationSession<GridCommand, GridGameView>(GridGameRules.Create(definition), 42);
            Fixtures.Execute(session, new Move(Side.A, 1, new GridPosition(1, 0)));
            session.Submit(new Attack(Side.A, 1, 2));
            session.Submit(new EndTurn(Side.A));
            session.Submit(new Move(Side.A, 1, new GridPosition(1, 1)));
            CommandResult lethal = Assert.IsType<StepResult>(session.Advance()).Result;
            Assert.Collection(lethal.Events,
                value => { UnitDamaged damaged = Assert.IsType<UnitDamaged>(value); Assert.Equal(0, damaged.Health); Assert.Equal(4, damaged.Damage); },
                value => Assert.Equal(Side.A, Assert.IsType<GameEnded>(value).Winner));
            string final = Fixtures.State(session.GetState());
            Assert.Equal("GameFinished", Assert.IsType<StepResult>(session.Advance()).Result.Error);
            Assert.Equal("GameFinished", Assert.IsType<StepResult>(session.Advance()).Result.Error);
            Assert.Equal(final, Fixtures.State(session.GetState()));
        }

        [Fact]
        public void Create_CopiesCollectionsAndViewsCannotMutateState()
        {
            ScenarioDefinition source = Fixtures.Definition();
            var units = source.Units.ToList();
            var obstacles = source.Obstacles.ToList();
            GridGameRules rules = GridGameRules.Create(new ScenarioDefinition(5, 5, obstacles, units, 2, 2));
            var session = new SimulationSession<GridCommand, GridGameView>(rules, 42);
            string before = Fixtures.State(session.GetState());
            units.Clear();
            obstacles.Clear();
            GridGameView view = session.GetState();
            Assert.Throws<NotSupportedException>(() => ((IList<UnitDefinition>)view.Units).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<GridPosition>)view.Obstacles).Clear());
            Assert.Equal(before, Fixtures.State(session.GetState()));
            Assert.Equal(42UL, session.RandomState);
        }
    }
}
