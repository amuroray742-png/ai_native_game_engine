using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    internal static class Fixtures
    {
        internal static string DemoPath => Path.Combine(AppContext.BaseDirectory, "demo.json");
        internal static ScenarioDefinition Definition() => ScenarioLoader.Load(DemoPath);
        internal static SimulationSession<GridCommand, GridGameView> Session() =>
            new SimulationSession<GridCommand, GridGameView>(GridGameRules.Create(Definition()), 42);
        internal static string State(GridGameView view) => JsonSerializer.Serialize(view);
        internal static string Trace(DemoStep step) => JsonSerializer.Serialize(new
        {
            step.Step.Sequence,
            step.Step.Result.Error,
            State = State(step.State),
            Events = step.Step.Result.Events.Select(value => new
            {
                Type = value.GetType().Name,
                Data = JsonSerializer.Serialize(value, value.GetType())
            }).ToArray()
        });

        internal static CommandResult Execute(SimulationSession<GridCommand, GridGameView> session, GridCommand command)
        {
            session.Submit(command);
            return Assert.IsType<StepResult>(session.Advance()).Result;
        }

        internal static void AssertRejected(SimulationSession<GridCommand, GridGameView> session, GridCommand command, string error)
        {
            string before = State(session.GetState());
            ulong random = session.RandomState;
            CommandResult result = Execute(session, command);
            Assert.False(result.Succeeded);
            Assert.Equal(error, result.Error);
            Assert.Empty(result.Events);
            Assert.Equal(before, State(session.GetState()));
            Assert.Equal(random, session.RandomState);
        }
    }
}
