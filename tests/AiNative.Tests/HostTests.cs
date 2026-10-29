using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiNative.GridGame;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class HostTests
    {
        [Fact]
        public void Demo_CompletesWithExpectedEventsAndOutcome()
        {
            var trace = DemoRunner.Run(Fixtures.Definition());
            Assert.True(DemoRunner.HasExpectedOutcome(trace));
            Assert.Equal(6, trace.Count);
            Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, trace.Select(step => step.Step.Sequence));
            Assert.IsType<UnitMoved>(Assert.Single(trace[0].Step.Result.Events));
            Assert.Equal(2, Assert.IsType<UnitDamaged>(Assert.Single(trace[1].Step.Result.Events)).Health);
            Assert.Equal(Side.B, Assert.IsType<TurnChanged>(Assert.Single(trace[2].Step.Result.Events)).Side);
            Assert.Equal(2, Assert.IsType<UnitDamaged>(Assert.Single(trace[3].Step.Result.Events)).Health);
            Assert.Equal(Side.A, Assert.IsType<TurnChanged>(Assert.Single(trace[4].Step.Result.Events)).Side);
            Assert.Collection(trace[5].Step.Result.Events,
                value => Assert.Equal(0, Assert.IsType<UnitDamaged>(value).Health),
                value => Assert.Equal(Side.A, Assert.IsType<GameEnded>(value).Winner));
        }

        [Fact]
        public void Demo_WhenRepeated_ProducesIdenticalStatesAndEvents()
        {
            var first = DemoRunner.Run(Fixtures.Definition()).Select(Fixtures.Trace).ToArray();
            for (int index = 0; index < 10; index++)
            {
                Assert.Equal(first, DemoRunner.Run(Fixtures.Definition()).Select(Fixtures.Trace).ToArray());
            }
        }

        [Fact]
        public void Run_DefaultAndExplicitPath_Succeed()
        {
            foreach (string[] args in new[] { Array.Empty<string>(), new[] { Fixtures.DemoPath } })
            {
                var output = new StringWriter();
                var error = new StringWriter();
                Assert.Equal(0, Program.Run(args, output, error));
                string initial = string.Join(Environment.NewLine,
                    "Initial state:",
                    "  Map=5x5 Obstacles=[(2,2)] Seed=42",
                    "  A id=1 position=(0,0) health=4",
                    "  B id=2 position=(2,0) health=4",
                    "  Turn=1 Active=A AP=2 Winner=None",
                    "Step 1:");
                Assert.StartsWith(initial, output.ToString());
                Assert.Contains("Demo passed", output.ToString());
                Assert.Contains("Step 1: Move side=A unit=1 direction=Right => Accepted", output.ToString());
                Assert.DoesNotContain("from=", output.ToString());
                Assert.DoesNotContain("to=", output.ToString());
                Assert.Contains("Step 4: Attack side=B unit=2 target=1 => Accepted", output.ToString());
                Assert.Contains("Step 5: EndTurn side=B => Accepted", output.ToString());
                Assert.Equal("", error.ToString());
            }
        }

        [Fact]
        public void Run_InvalidInput_ReturnsConfigurationFailure()
        {
            Assert.Equal(2, Program.Run(new[] { "a", "b" }, new StringWriter(), new StringWriter()));
            Assert.Equal(2, Program.Run(new[] { Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) }, new StringWriter(), new StringWriter()));
            ScenarioTests.WithJson("{}", path =>
            {
                var error = new StringWriter();
                Assert.Equal(2, Program.Run(new[] { path }, new StringWriter(), error));
                Assert.Contains("Configuration error", error.ToString());
            });
        }

        [Theory]
        [InlineData("2", "1")]
        [InlineData("2", "10")]
        public void Run_UnexpectedOutcomeOrRejectedCommand_ReturnsFailure(string oldDamage, string damage)
        {
            string json = File.ReadAllText(Fixtures.DemoPath).Replace($"\"attackDamage\": {oldDamage}", $"\"attackDamage\": {damage}");
            ScenarioTests.WithJson(json, path =>
            {
                var error = new StringWriter();
                Assert.Equal(1, Program.Run(new[] { path }, new StringWriter(), error));
                Assert.Contains("Demo failed", error.ToString());
            });
        }

        [Fact]
        public void Run_WhenOutputFails_ReturnsExecutionFailure()
        {
            var error = new StringWriter();
            Assert.Equal(1, Program.Run(Array.Empty<string>(), new BrokenOutput(), error));
            Assert.Contains("Simulation failure", error.ToString());
            Assert.DoesNotContain("Configuration error", error.ToString());
        }

        private sealed class BrokenOutput : StringWriter
        {
            public override void WriteLine(string? value)
            {
                throw new IOException("Output unavailable");
            }
        }

        [Fact]
        public async Task Executable_FromAnotherWorkingDirectory_ReturnsSuccess()
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.Equal("", await error);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Demo passed", await output);
        }
    }
}
