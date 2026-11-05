using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using AiNative.Hosting;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using AiNative.PhysicsSandbox;
using AiNative.PhysicsSandbox.Hosting;
using Xunit;

namespace AiNative.Tests
{
    public sealed class PhysicsPlayTests
    {
        [Fact]
        public void LiveGeometrySurvivesRestoreAndQueriesLeaveV2StateUntouched()
        {
            var module = new PhysicsSandboxModule(new JoltWorldFactory());
            var config = module.ReadConfiguration(Fixtures.Read("physics.scenario.json").GetProperty("configuration"));
            using var game = module.CreatePlay(config, 42);
            game.Submit(new SpawnBody(ShapeKind.Box, new Vector3(.4f), new Vector3(2, 4, 0)));
            var first = game.AdvanceTick().Observation;
            Assert.Equal(ShapeKind.Box, first.State[2].Shape);
            Assert.Equal(new Vector3(.4f), first.State[2].Size);
            game.Submit(new RemoveBody(3));
            game.Submit(new SpawnBody(ShapeKind.Sphere, new Vector3(.3f), new Vector3(2, 4, 0)));
            game.AdvanceTick();
            var snapshot = StateCodec.Encode(module, config, game, Fixtures.Environment());
            var saved = StateCodec.Read(snapshot, new GameRegistry(module));
            using var restored = (SandboxHost)StateCodec.Restore(saved, Fixtures.Environment());
            var body = restored.CaptureObservation().State[2];
            Assert.Equal(4UL, body.Id);
            Assert.Equal(ShapeKind.Sphere, body.Shape);
            Assert.Equal(new Vector3(.3f), body.Size);
            Assert.Equal("Sphere", restored.Observe()[2].GetProperty("shape").GetString());
            Assert.False(snapshot.GetProperty("state").GetProperty("bodies")[2].TryGetProperty("shape", out _));
            Assert.Equal(snapshot.GetProperty("stateHash").GetString(),
                StateCodec.Encode(module, config, restored, Fixtures.Environment()).GetProperty("stateHash").GetString());
            Assert.Equal(3UL, first.State[2].Id);
        }

        [Fact]
        public void RecorderRejectsMissingDuplicateAndLateStepsWithoutPublishingThem()
        {
            var module = new PhysicsSandboxModule(new JoltWorldFactory());
            var config = module.ReadConfiguration(Fixtures.Read("physics.scenario.json").GetProperty("configuration"));
            using var session = module.CreatePlay(config, 42);
            var trace = new HostTraceRecorder(module, config, session, Fixtures.Environment());
            var tick1 = session.Advance();
            trace.Advanced(tick1);
            Assert.Throws<ProtocolException>(() => trace.Advanced(tick1));
            Assert.Equal(1, trace.Count);
            var tick2 = session.Advance();
            var tick3 = session.Advance();
            Assert.Throws<ProtocolException>(() => trace.Advanced(tick2));
            Assert.Throws<ProtocolException>(() => trace.Advanced(tick3));
            Assert.Throws<ProtocolException>(() => trace.Finish());
            Assert.Equal(1, trace.Count);
        }

        [Theory]
        [InlineData("-0", int.MinValue)]
        [InlineData("7.038531E-26", 0x15ae43fd)]
        [InlineData("1.0000000596046447753906250000000001", 0x3f800001)]
        [InlineData("1.0000001788139343261718749999999999", 0x3f800001)]
        [InlineData("1.000000059604644775390625", 0x3f800000)]
        [InlineData("1e9999999999999999", 0x7f800000)]
        [InlineData("-1e-9999999999999999", int.MinValue)]
        public void DecimalReaderRoundsOnceAndPreservesNegativeZero(string json, int expected)
        {
            Assert.Equal(expected, BitConverter.SingleToInt32Bits(PhysicsJsonFloat.Parse(json)));
        }

        [Fact]
        public void PortableFloatEncodingMatchesFrameworkForEdgesAndSeededBitPatterns()
        {
            var bits = new List<int> { 0, int.MinValue, 0x7f7fffff, 1, 0x7fffff };
            for (int exponent = 1; exponent < 255; exponent++)
            {
                int power = exponent << 23;
                bits.Add(power - 1);
                bits.Add(power);
                bits.Add(power + 1);
            }
            var random = new Random(42);
            for (int i = 0; i < 20000; i++)
            {
                bits.Add(random.Next(0x7f800000) | (i % 2 == 0 ? int.MinValue : 0));
            }
            foreach (int pattern in bits)
            {
                float value = BitConverter.Int32BitsToSingle(pattern);
                string json = JsonSerializer.Serialize(value);
                Assert.Equal(json, PhysicsJsonFloat.Format(value));
                Assert.Equal(pattern, BitConverter.SingleToInt32Bits(PhysicsJsonFloat.Parse(json)));
            }
        }
    }
}
