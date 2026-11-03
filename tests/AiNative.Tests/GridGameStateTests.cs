using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;

using Xunit;

namespace AiNative.Tests
{
    public sealed class GridGameStateTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void Restore_AtEveryDemoBoundary_MatchesContinuousExecution(int boundary)
        {
            GridGameRules rules = GridGameRules.Create(Fixtures.Definition());
            var original = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => rules, 42, new SimulationTiming(0));
            GridCommand[] commands =
            {
                new Move(Side.A, 1, MoveDirection.Right), new Attack(Side.A, 1, 2),
                new EndTurn(Side.A), new Attack(Side.B, 2, 1),
                new EndTurn(Side.B), new Attack(Side.A, 1, 2)
            };
            for (int index = 0; index < boundary; index++)
            {
                Fixtures.Execute(original, commands[index]);
            }
            SimulationSnapshot<GridGameState> sessionState = original.ExportState();
            GridGameState gameState = rules.ExportState();
            string saved = JsonSerializer.Serialize(gameState);
            GridGameRules restoredRules = GridGameRules.Restore(gameState);
            var restored = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Restore(sessionState, GridGameRules.Restore);
            Assert.Equal(Fixtures.State(original.GetState()), Fixtures.State(restored.GetState()));
            Assert.Equal(boundary, restored.Tick);
            Assert.Equal(0, restored.PendingCount);
            for (int index = boundary; index < commands.Length; index++)
            {
                Assert.Equal(index + 1, original.Submit(commands[index]));
                Assert.Equal(index + 1, restored.Submit(commands[index]));
                InputResult expected = Assert.Single(original.Advance().Inputs);
                InputResult actual = Assert.Single(restored.Advance().Inputs);
                Assert.Equal(Fixtures.Trace(expected, original.GetState()),
                    Fixtures.Trace(actual, restored.GetState()));
                Assert.Equal(original.RandomState, restored.RandomState);
                Assert.Equal(original.Tick, restored.Tick);
            }
            GridGameView final = restored.GetState();
            Assert.Equal(Side.A, final.Winner);
            Assert.Equal(3, final.TurnIndex);
            Assert.Equal(1, final.ActionPoints);
            Assert.Equal(2, final.Units[0].Health);
            Assert.Equal(0, final.Units[1].Health);
            Fixtures.AssertRejected(restored, new EndTurn(Side.A), "GameFinished");
            Assert.Equal(7, restored.Tick);
            Assert.Equal(6, original.Tick);
            Assert.Equal(saved, JsonSerializer.Serialize(gameState));
            Assert.Equal(4, restoredRules.ExportState().Configuration.Units[1].Health);
            Assert.Equal(new GridPosition(0, 0), restoredRules.ExportState().Configuration.Units[0].Position);
        }

        [Fact]
        public void Restore_PreservesNonDefaultConfigurationAndIndependentInstances()
        {
            var configuration = new ScenarioDefinition(8, 7, new[] { new GridPosition(6, 6) },
                new[] { new UnitDefinition(20, Side.B, new GridPosition(2, 0), 7),
                    new UnitDefinition(10, Side.A, new GridPosition(0, 0), 6) },
                actionPointsPerTurn: 3, attackDamage: 1);
            GridGameRules rules = GridGameRules.Create(configuration);
            var original = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => rules, 0, new SimulationTiming(0));
            Fixtures.Execute(original, new Move(Side.A, 10, MoveDirection.Right));
            Fixtures.Execute(original, new Attack(Side.A, 10, 20));
            GridGameRules restoredRules = GridGameRules.Restore(rules.ExportState());
            var restored = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Restore(original.ExportState(), GridGameRules.Restore);
            Fixtures.Execute(restored, new Attack(Side.A, 10, 20));
            Assert.Equal(5, restored.GetState().Units[1].Health);
            Assert.Equal(6, original.GetState().Units[1].Health);
            Fixtures.Execute(restored, new EndTurn(Side.A));
            Assert.Equal(3, restored.GetState().ActionPoints);
            Assert.Equal(Side.B, restored.GetState().ActiveSide);
            Assert.Equal(8, restored.GetState().Width);
            Assert.Equal(7, restored.GetState().Height);
            Assert.Equal(new GridPosition(6, 6), Assert.Single(restored.GetState().Obstacles));
        }

        [Fact]
        public void State_CopiesInputCollectionsAndExposesOnlyReadOnlyCollections()
        {
            ScenarioDefinition source = Fixtures.Definition();
            var initialUnits = source.Units.Reverse().ToList();
            var currentUnits = source.Units.Reverse().ToList();
            var obstacles = source.Obstacles.ToList();
            var configuration = new ScenarioDefinition(5, 5, obstacles, initialUnits, 2, 2);
            var state = new GridGameState(configuration, currentUnits, Side.A, 2, 1, null);
            GridGameRules rules = GridGameRules.Restore(state);
            initialUnits.Clear();
            currentUnits.Clear();
            obstacles.Clear();
            Assert.Equal(new[] { 1, 2 }, state.Units.Select(unit => unit.Id));
            Assert.Equal(new[] { 1, 2 }, state.Configuration.Units.Select(unit => unit.Id));
            Assert.Throws<NotSupportedException>(() => ((IList<UnitDefinition>)state.Units).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<UnitDefinition>)state.Configuration.Units).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<GridPosition>)state.Configuration.Obstacles).Clear());
            var session = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => rules, 42, new SimulationTiming(0));
            string before = JsonSerializer.Serialize(rules.ExportState());
            for (int index = 0; index < 3; index++)
            {
                session.GetState();
                session.ExportState();
                Assert.Equal(before, JsonSerializer.Serialize(rules.ExportState()));
            }
            Fixtures.Execute(session, new Move(Side.A, 1, MoveDirection.Right));
            Assert.Equal(new GridPosition(0, 0), state.Units[0].Position);
            Assert.Equal(new GridPosition(0, 0), rules.ExportState().Configuration.Units[0].Position);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("extra")]
        [InlineData("duplicate")]
        [InlineData("unknownId")]
        [InlineData("wrongSide")]
        [InlineData("invalidSide")]
        [InlineData("negativeHealth")]
        [InlineData("excessHealth")]
        [InlineData("deadWithoutWinner")]
        [InlineData("bothDead")]
        [InlineData("outside")]
        [InlineData("negativePosition")]
        [InlineData("obstacle")]
        [InlineData("overlap")]
        [InlineData("negativeAp")]
        [InlineData("excessAp")]
        [InlineData("zeroTurn")]
        [InlineData("negativeTurn")]
        [InlineData("wrongActiveSide")]
        [InlineData("invalidActiveSide")]
        [InlineData("winnerAliveOpponent")]
        [InlineData("deadWinner")]
        [InlineData("wrongWinner")]
        [InlineData("invalidWinner")]
        [InlineData("invalidConfiguration")]
        public void Restore_InvalidState_IsRejectedWithoutChangingExistingSession(string variant)
        {
            GridGameRules rules = GridGameRules.Create(Fixtures.Definition());
            var session = SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => rules, 42, new SimulationTiming(0));
            Fixtures.Execute(session, new Move(Side.A, 1, MoveDirection.Up));
            string before = Fixtures.State(session.GetState());
            GridGameState state = InvalidState(variant);
            Assert.Throws<ArgumentException>(() => GridGameRules.Restore(state));
            Assert.Equal(before, Fixtures.State(session.GetState()));
            Assert.Equal(42UL, session.RandomState);
            Assert.Equal(1, session.Tick);
            Assert.Equal(2, session.Submit(new EndTurn(Side.A)));
            Assert.True(Assert.Single(session.Advance().Inputs).Result.Succeeded);
        }

        [Fact]
        public void Restore_BWinnerAndLargeTurn_ArePreserved()
        {
            ScenarioDefinition configuration = Fixtures.Definition();
            UnitDefinition[] units = { new UnitDefinition(1, Side.A, new GridPosition(0, 0), 0), configuration.Units[1] };
            var state = new GridGameState(configuration, units, Side.B, 0, long.MaxValue - 1, Side.B);
            GridGameRules restored = GridGameRules.Restore(state);
            Assert.Equal(Side.B, restored.GetState().Winner);
            Assert.Equal(long.MaxValue - 1, restored.GetState().TurnIndex);
        }

        [Fact]
        public void State_NullInputs_AreRejectedBeforeCopying()
        {
            ScenarioDefinition configuration = Fixtures.Definition();
            Assert.Throws<ArgumentNullException>(() => GridGameRules.Restore(null!));
            Assert.Throws<ArgumentNullException>(() => new GridGameState(null!, configuration.Units, Side.A, 2, 1, null));
            Assert.Throws<ArgumentNullException>(() => new GridGameState(configuration, null!, Side.A, 2, 1, null));
            Assert.Throws<ArgumentException>(() => new GridGameState(configuration, new UnitDefinition[] { null! }, Side.A, 2, 1, null));
            var nullObstacles = new ScenarioDefinition(5, 5, null!, configuration.Units, 2, 2);
            Assert.Throws<ArgumentNullException>(() => new GridGameState(nullObstacles, configuration.Units, Side.A, 2, 1, null));
            var nullUnits = new ScenarioDefinition(5, 5, configuration.Obstacles, null!, 2, 2);
            Assert.Throws<ArgumentNullException>(() => new GridGameState(nullUnits, configuration.Units, Side.A, 2, 1, null));
        }

        private static GridGameState InvalidState(string variant)
        {
            ScenarioDefinition configuration = Fixtures.Definition();
            var units = configuration.Units.ToList();
            Side active = Side.A;
            int ap = 2;
            long turn = 1;
            Side? winner = null;
            switch (variant)
            {
                case "missing":
                    units.RemoveAt(1);
                    break;
                case "extra":
                    units.Add(units[0]);
                    break;
                case "duplicate":
                    units[1] = units[0];
                    break;
                case "unknownId":
                    units[0] = new UnitDefinition(99, Side.A, new GridPosition(0, 0), 4);
                    break;
                case "wrongSide":
                    units[0] = new UnitDefinition(1, Side.B, new GridPosition(0, 0), 4);
                    break;
                case "invalidSide":
                    units[0] = new UnitDefinition(1, (Side)99, new GridPosition(0, 0), 4);
                    break;
                case "negativeHealth":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, 0), -1);
                    break;
                case "excessHealth":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, 0), 5);
                    break;
                case "deadWithoutWinner":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, 0), 0);
                    break;
                case "bothDead":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, 0), 0);
                    units[1] = new UnitDefinition(2, Side.B, new GridPosition(2, 0), 0);
                    winner = Side.A;
                    break;
                case "outside":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(5, 0), 4);
                    break;
                case "negativePosition":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, -1), 4);
                    break;
                case "obstacle":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(2, 2), 4);
                    break;
                case "overlap":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(2, 0), 4);
                    break;
                case "negativeAp":
                    ap = -1;
                    break;
                case "excessAp":
                    ap = 3;
                    break;
                case "zeroTurn":
                    turn = 0;
                    break;
                case "negativeTurn":
                    turn = -1;
                    break;
                case "wrongActiveSide":
                    active = Side.B;
                    break;
                case "invalidActiveSide":
                    active = (Side)99;
                    break;
                case "winnerAliveOpponent":
                    winner = Side.A;
                    break;
                case "deadWinner":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, 0), 0);
                    winner = Side.A;
                    break;
                case "wrongWinner":
                    units[0] = new UnitDefinition(1, Side.A, new GridPosition(0, 0), 0);
                    winner = Side.B;
                    break;
                case "invalidWinner":
                    winner = (Side)99;
                    break;
                case "invalidConfiguration":
                    configuration = new ScenarioDefinition(0, 5, configuration.Obstacles, configuration.Units, 2, 2);
                    break;
                default:
                    throw new ArgumentException("Unknown test variant.", nameof(variant));
            }
            return new GridGameState(configuration, units, active, ap, turn, winner);
        }
    }
}
