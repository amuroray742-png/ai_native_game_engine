using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;
using AiNative.Physics;

namespace AiNative.PhysicsSandbox.Hosting
{
    public sealed class PhysicsSandboxModule : IGameModule
    {
        private readonly IPhysicsWorldFactory _factory;
        public PhysicsSandboxModule(IPhysicsWorldFactory factory, params string[] nativeLibraries)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            CodeTypes = Array.AsReadOnly(new[] { typeof(PhysicsSandboxModule), typeof(SandboxSimulation), typeof(IPhysicsWorld), factory.GetType() });
            NativeLibraries = Array.AsReadOnly((string[])nativeLibraries.Clone());
        }
        public string Id => "physics-sandbox";
        public int Version => 1;
        public SimulationTiming Timing { get; } = new SimulationTiming(60);
        public IReadOnlyList<Type> CodeTypes { get; }
        public IReadOnlyList<string> NativeLibraries { get; }
        public JsonElement Description => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", Id);
            writer.WriteNumber("version", Version);
            writer.WriteString("timing", "fixed");
            writer.WriteNumber("ticksPerSecond", 60);
            writer.WriteString("physics", _factory.Backend);
            writer.WriteStartArray("commands");
            writer.WriteStringValue("Spawn(shape: Sphere|Box, size[3], position[3])");
            writer.WriteStringValue("Remove(id)");
            writer.WriteStringValue("Impulse(id, impulse[3])");
            writer.WriteEndArray();
            writer.WriteString("observation", "stable body IDs, pose, velocity, active state; ordered contact events");
            writer.WriteEndObject();
        });

        public JsonElement ReadConfiguration(JsonElement value) => Input(() =>
        {
            IReadOnlyList<BodyDefinition> bodies = SandboxData.ReadConfiguration(value);
            return JsonData.Write(writer => SandboxData.WriteConfiguration(writer, bodies));
        }, "InvalidConfiguration");

        public JsonElement ReadCommand(JsonElement value) => Input(() =>
        {
            SandboxCommand command = SandboxData.ReadCommand(value);
            return JsonData.Write(writer => SandboxData.WriteCommand(writer, command));
        }, "InvalidProtocol");

        public JsonElement ReadState(JsonElement configuration, JsonElement state, long processed)
        {
            return Input(() =>
            {
                SandboxData.ReadConfiguration(configuration);
                SandboxState snapshot = SandboxData.ReadState(state, _factory.Backend);
                _factory.ValidateSnapshot(snapshot.Physics, snapshot.Bodies);
                return JsonData.Write(writer => SandboxData.WriteState(writer, snapshot));
            }, "InvalidState");
        }

        public void ValidateResult(JsonElement result) => SandboxData.ValidateResult(result);

        public IHostedGame Create(JsonElement configuration, ulong seed)
        {
            IReadOnlyList<BodyDefinition> bodies = SandboxData.ReadConfiguration(ReadConfiguration(configuration));
            return new SandboxHost(SimulationRuntime<SandboxCommand, IReadOnlyList<BodyState>, SandboxState>.Create(
                () => SandboxSimulation.Create(_factory, bodies), seed, Timing));
        }

        public IHostedGame Restore(JsonElement configuration, JsonElement state, RuntimeState runtime)
        {
            state = ReadState(configuration, state, runtime.Processed);
            JsonData.Require(runtime.Timing.TicksPerSecond == 60, "TimingMismatch", "$.runtime.ticksPerSecond");
            SandboxState snapshot = SandboxData.ReadState(state, _factory.Backend);
            return new SandboxHost(SimulationRuntime<SandboxCommand, IReadOnlyList<BodyState>, SandboxState>.Restore(
                new SimulationSnapshot<SandboxState>(runtime, snapshot), data => SandboxSimulation.Restore(_factory, data)));
        }

        private static T Input<T>(Func<T> action, string code)
        {
            try
            {
                return action();
            }
            catch (ArgumentException error) { throw new ProtocolException(code, error.Message, "$", error); }
            catch (OverflowException error) { throw new ProtocolException(code, error.Message, "$", error); }
        }
    }

    internal sealed class SandboxHost : HostedGame<SandboxCommand, IReadOnlyList<BodyState>, SandboxState>
    {
        internal SandboxHost(SimulationRuntime<SandboxCommand, IReadOnlyList<BodyState>, SandboxState> runtime) : base(runtime) { }
        protected override SandboxCommand ReadCommand(JsonElement command) => SandboxData.ReadCommand(command);
        protected override void WriteObservation(Utf8JsonWriter writer, IReadOnlyList<BodyState> bodies) => SandboxData.WriteBodies(writer, bodies);
        protected override void WriteState(Utf8JsonWriter writer, SandboxState state) => SandboxData.WriteState(writer, state);
        protected override void WriteEvent(Utf8JsonWriter writer, GameEvent value) => SandboxData.WriteEvent(writer, value);
    }
}
