using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.Headless;
using Xunit;

namespace AiNative.Tests
{
    public sealed class CanonicalStateTests
    {
        [Theory]
        [InlineData("configuration.canonical.json")]
        [InlineData("initial-state.canonical.json")]
        [InlineData("moved-state.canonical.json")]
        [InlineData("finished-state.canonical.json")]
        [InlineData("random-max.canonical.json")]
        [InlineData("build-identity.txt")]
        public void CanonicalBytes_MatchIndependentFixedVectors(string file)
        {
            ScenarioDefinition config = Fixtures.Definition();
            UnitDefinition[] units = config.Units.ToArray();
            int actionPoints = 2;
            long turn = 1;
            Side? winner = null;
            ulong random = 42;
            if (file == "moved-state.canonical.json" || file == "finished-state.canonical.json")
            {
                units[0] = new UnitDefinition(1, Side.A, new GridPosition(1, 0), file == "finished-state.canonical.json" ? 2 : 4);
                actionPoints = 1;
            }
            if (file == "finished-state.canonical.json")
            {
                units[1] = new UnitDefinition(2, Side.B, new GridPosition(2, 0), 0);
                turn = 3;
                winner = Side.A;
            }
            if (file == "random-max.canonical.json")
            {
                random = ulong.MaxValue;
            }
            byte[] actual = file switch
            {
                "configuration.canonical.json" => CanonicalState.ConfigurationBytes(config),
                "build-identity.txt" => SnapshotEnvironment.BuildIdentityBytes(new string('0', 64), new string('1', 64), new string('2', 64)),
                _ => CanonicalState.StateBytes(new GridGameState(config, units, Side.A, actionPoints, turn, winner),
                    DeterministicRandom.Algorithm, random)
            };
            Assert.Equal(SnapshotFixtures.Read(file), actual);
            using JsonDocument manifest = JsonDocument.Parse(SnapshotFixtures.Read("hash-vectors.json"));
            JsonElement vector = manifest.RootElement.GetProperty("vectors").EnumerateArray()
                .Single(item => item.GetProperty("file").GetString() == file);
            Assert.Equal(vector.GetProperty("byteLength").GetInt32(), actual.Length);
            Assert.Equal(vector.GetProperty("sha256").GetString(), CanonicalState.Hash(actual));
        }

        [Fact]
        public void CanonicalBytes_IgnoreInputOrderAndCultureButCoverStateAndConfiguration()
        {
            ScenarioDefinition original = Fixtures.Definition();
            GridPosition[] obstacles = { new GridPosition(3, 1), new GridPosition(2, 2), new GridPosition(1, 1) };
            var config = new ScenarioDefinition(5, 5, obstacles, original.Units, 2, 2);
            var reordered = new ScenarioDefinition(5, 5, obstacles.Reverse().ToArray(), original.Units.Reverse().ToArray(), 2, 2);
            var game = new GridGameState(config, config.Units, Side.A, 2, 1, null);
            byte[] expected = CanonicalState.StateBytes(game, DeterministicRandom.Algorithm, 42);
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                Assert.Equal(CanonicalState.ConfigurationBytes(config), CanonicalState.ConfigurationBytes(reordered));
                Assert.Equal(expected, CanonicalState.StateBytes(new GridGameState(reordered, reordered.Units, Side.A, 2, 1, null),
                    DeterministicRandom.Algorithm, 42));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
            string hash = CanonicalState.Hash(expected);
            Assert.NotEqual(hash, CanonicalState.Hash(CanonicalState.StateBytes(game, DeterministicRandom.Algorithm, 43)));
            Assert.NotEqual(hash, CanonicalState.Hash(CanonicalState.StateBytes(
                new GridGameState(config, config.Units, Side.A, 1, 1, null), DeterministicRandom.Algorithm, 42)));
            var changedConfig = new ScenarioDefinition(5, 5, obstacles, config.Units, 2, 3);
            Assert.NotEqual(hash, CanonicalState.Hash(CanonicalState.StateBytes(
                new GridGameState(changedConfig, config.Units, Side.A, 2, 1, null), DeterministicRandom.Algorithm, 42)));
        }

        [Fact]
        public void CurrentEnvironment_UsesActualAssemblyContentAndRuntime()
        {
            SnapshotEnvironment environment = SnapshotEnvironment.Current();
            string[] paths = { typeof(SessionState).Assembly.Location, typeof(GridGameRules).Assembly.Location, typeof(Program).Assembly.Location };
            string[] hashes = paths.Select(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            string combined = $"AiNative.Core={hashes[0]}\nAiNative.GridGame={hashes[1]}\nAiNative.Headless={hashes[2]}\n";
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined))), environment.BuildHash);
            Assert.Equal(Environment.Version.ToString(), environment.RuntimeVersion);
            Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), environment.Architecture);
            Assert.Equal(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux", environment.Os);
        }
    }
}
