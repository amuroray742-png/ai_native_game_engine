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
            CommandResult result = Fixtures.Execute(session, new Move(Side.A, 1, MoveDirection.Right));
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
        [InlineData(MoveDirection.Up, 1, 2)]
        [InlineData(MoveDirection.Down, 1, 0)]
        [InlineData(MoveDirection.Left, 0, 1)]
        [InlineData(MoveDirection.Right, 2, 1)]
        public void Move_InEachDirection_AdvancesOneGridCell(MoveDirection direction, int x, int y)
        {
            var session = CreateSession(new GridPosition(1, 1));
            CommandResult result = Fixtures.Execute(session, new Move(Side.A, 1, direction));
            UnitMoved moved = Assert.IsType<UnitMoved>(Assert.Single(result.Events));
            Assert.Equal(new GridPosition(1, 1), moved.From);
            Assert.Equal(new GridPosition(x, y), moved.To);
            Assert.Equal(moved.To, session.GetState().Units[0].Position);
            Assert.Equal(1, session.GetState().ActionPoints);
        }

        [Theory]
        [InlineData(0, 0, MoveDirection.Left)]
        [InlineData(0, 0, MoveDirection.Down)]
        [InlineData(4, 4, MoveDirection.Right)]
        [InlineData(4, 4, MoveDirection.Up)]
        public void Move_OutsideEachMapEdge_LeavesStateUnchanged(int x, int y, MoveDirection direction)
        {
            Fixtures.AssertRejected(CreateSession(new GridPosition(x, y)), new Move(Side.A, 1, direction), "OutOfBounds");
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(int.MaxValue)]
        public void Move_WithInvalidDirection_LeavesStateUnchanged(int direction)
        {
            Fixtures.AssertRejected(Fixtures.Session(), new Move(Side.A, 1, (MoveDirection)direction), "InvalidDirection");
        }

        [Fact]
        public void Move_IntoObstacleOrUnit_LeavesStateUnchanged()
        {
            Fixtures.AssertRejected(CreateSession(new GridPosition(1, 2)), new Move(Side.A, 1, MoveDirection.Right), "Blocked");
            Fixtures.AssertRejected(CreateSession(new GridPosition(1, 0)), new Move(Side.A, 1, MoveDirection.Right), "Occupied");
        }

        [Fact]
        public void QueuedMoves_UsePositionAtExecutionTime()
        {
            var session = Fixtures.Session();
            session.Submit(new Move(Side.A, 1, MoveDirection.Up));
            session.Submit(new Move(Side.A, 1, MoveDirection.Up));
            TickResult<GridGameView> frame = session.Advance();
            CommandResult result = frame.Inputs[1].Result;
            UnitMoved moved = Assert.IsType<UnitMoved>(Assert.Single(result.Events));
            Assert.Equal(new GridPosition(0, 1), moved.From);
            Assert.Equal(new GridPosition(0, 2), moved.To);
            Assert.Equal(moved.To, session.GetState().Units[0].Position);
            Assert.Equal(0, session.GetState().ActionPoints);
        }

        private static SimulationRuntime<GridCommand, GridGameView, GridGameState> CreateSession(GridPosition origin)
        {
            ScenarioDefinition source = Fixtures.Definition();
            UnitDefinition[] units = { new UnitDefinition(1, Side.A, origin, 4), source.Units[1] };
            var definition = new ScenarioDefinition(5, 5, source.Obstacles, units, 2, 2);
            return Fixtures.Session(definition);
        }

        [Fact]
        public void Commands_WhenSideOrEntityInvalid_LeaveStateUnchanged()
        {
            var session = Fixtures.Session();
            Fixtures.AssertRejected(session, new Move(Side.B, 2, MoveDirection.Right), "NotYourTurn");
            Fixtures.AssertRejected(session, new Move(Side.A, 2, MoveDirection.Right), "NotYourUnit");
            Fixtures.AssertRejected(session, new Move(Side.A, 99, MoveDirection.Right), "UnknownUnit");
            Fixtures.AssertRejected(session, new EndTurn(Side.B), "NotYourTurn");
            Fixtures.AssertRejected(session, new EndTurn((Side)999), "NotYourTurn");
        }

        [Fact]
        public void ActionPoints_WhenExhausted_RequireExplicitEndTurn()
        {
            var session = Fixtures.Session();
            Fixtures.Execute(session, new Move(Side.A, 1, MoveDirection.Right));
            Fixtures.Execute(session, new Attack(Side.A, 1, 2));
            Assert.Equal(0, session.GetState().ActionPoints);
            Assert.Equal(Side.A, session.GetState().ActiveSide);
            Fixtures.AssertRejected(session, new Move(Side.A, 1, MoveDirection.Up), "NoActionPoints");
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
            session.Submit(new Move(Side.A, 1, MoveDirection.Right));
            session.Submit(new Move(Side.B, 2, MoveDirection.Right));
            TickResult<GridGameView> frame = session.Advance();
            Assert.True(frame.Inputs[0].Result.Succeeded);
            Assert.Equal("NotYourTurn", frame.Inputs[1].Result.Error);
            Assert.True(frame.Inputs[2].Result.Succeeded);
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
            var session = Fixtures.Session(definition);
            Fixtures.Execute(session, new Move(Side.A, 1, MoveDirection.Right));
            session.Submit(new Attack(Side.A, 1, 2));
            session.Submit(new EndTurn(Side.A));
            session.Submit(new Move(Side.A, 1, MoveDirection.Up));
            TickResult<GridGameView> frame = session.Advance();
            CommandResult lethal = frame.Inputs[0].Result;
            Assert.Collection(lethal.Events,
                value => { UnitDamaged damaged = Assert.IsType<UnitDamaged>(value); Assert.Equal(0, damaged.Health); Assert.Equal(4, damaged.Damage); },
                value => Assert.Equal(Side.A, Assert.IsType<GameEnded>(value).Winner));
            string final = Fixtures.State(session.GetState());
            Assert.Equal("GameFinished", frame.Inputs[1].Result.Error);
            Assert.Equal("GameFinished", frame.Inputs[2].Result.Error);
            Assert.Equal(final, Fixtures.State(session.GetState()));
        }

        [Fact]
        public void Create_CopiesCollectionsAndViewsCannotMutateState()
        {
            ScenarioDefinition source = Fixtures.Definition();
            var units = source.Units.ToList();
            var obstacles = source.Obstacles.ToList();
            GridGameRules rules = GridGameRules.Create(new ScenarioDefinition(5, 5, obstacles, units, 2, 2));
            var session = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => rules, 42, new SimulationTiming(0));
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
