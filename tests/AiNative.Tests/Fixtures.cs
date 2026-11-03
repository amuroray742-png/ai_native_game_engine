using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AiNative.Core;
using AiNative.Examples.Runner;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using AiNative.Hosting;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AiNative.Tests
{
    internal static class Fixtures
    {
        internal static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);
        internal static JsonElement Json(string text) => JsonData.Parse(Encoding.UTF8.GetBytes(text));
        internal static JsonElement Read(string name) => JsonData.Parse(File.ReadAllBytes(Data(name)));
        internal static JsonElement Configuration => Read("grid.scenario.json").GetProperty("configuration");
        internal static HostEnvironment Environment(string label = "test") => new HostEnvironment(label, "test-os", "x64", JsonData.EmptyObject);
        internal static GameRegistry Registry() => ExampleGames.CreateRegistry();
        internal static JsonElement LoadConfiguration(string path) => new GridGameModule().ReadConfiguration(JsonData.Parse(File.ReadAllBytes(path)));
        internal static ScenarioDefinition Definition() => new ScenarioDefinition(5, 5, new[] { new GridPosition(2, 2) },
            new[] { new UnitDefinition(1, Side.A, new GridPosition(0, 0), 4), new UnitDefinition(2, Side.B, new GridPosition(2, 0), 4) }, 2, 2);
        internal static SimulationRuntime<GridCommand, GridGameView, GridGameState> Session(ScenarioDefinition? configuration = null) =>
            SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => GridGameRules.Create(configuration ?? Definition()), 42, new SimulationTiming(0));
        internal static string State(GridGameView view) => JsonSerializer.Serialize(view);
        internal static string Trace(InputResult input, GridGameView view) => JsonSerializer.Serialize(new
        {
            input.Sequence,
            input.Result.Error,
            State = State(view),
            Events = input.Result.Events.Select(value => new { Type = value.GetType().Name, Data = JsonSerializer.Serialize(value, value.GetType()) })
        });
        internal static CommandResult Execute(SimulationRuntime<GridCommand, GridGameView, GridGameState> runtime, GridCommand command)
        {
            runtime.Submit(command);
            return Assert.Single(runtime.Advance().Inputs).Result;
        }
        internal static void AssertRejected(SimulationRuntime<GridCommand, GridGameView, GridGameState> runtime, GridCommand command, string error)
        {
            string before = State(runtime.GetState());
            ulong random = runtime.RandomState;
            CommandResult result = Execute(runtime, command);
            Assert.False(result.Succeeded);
            Assert.Equal(error, result.Error);
            Assert.Empty(result.Events);
            Assert.Equal(before, State(runtime.GetState()));
            Assert.Equal(random, runtime.RandomState);
        }
        internal static (int Code, JsonElement Result) Invoke(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int code = new CliRunner(Registry(), _ => Environment()).Run(args, output, error);
            return (code, Json(output.ToString()));
        }
        internal static RecordedRun Record(string game = "grid-game", ulong seed = 42, string environment = "test", JsonElement? commands = null)
        {
            IGameModule module = Registry().Get(game);
            string prefix = game == "grid-game" ? "grid" : "physics";
            JsonElement config = module.ReadConfiguration(Read(prefix + ".scenario.json").GetProperty("configuration"));
            using IHostedGame runtime = module.Create(config, seed);
            return HostVerification.Record(module, config, runtime, CommandFile.Read(commands ?? Read(prefix + ".commands.json"), module), Environment(environment));
        }
    }

    internal sealed class TestDirectory : IDisposable
    {
        private readonly string _temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        internal TestDirectory()
        {
            Path = System.IO.Path.Combine(_temporaryRoot, "ainative-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        internal string File(string name) => System.IO.Path.Combine(Path, name);
        internal string Write(string name, JsonElement value)
        {
            string file = File(name);
            System.IO.File.WriteAllBytes(file, JsonData.CanonicalBytes(value));
            return file;
        }
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(resolved).StartsWith("ainative-v2-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected test cleanup path");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
