using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;

namespace AiNative.GridGame.Hosting
{
    public sealed class GridGameModule : IGameModule
    {
        public string Id => "grid-game";
        public int Version => 1;
        public SimulationTiming Timing { get; } = new SimulationTiming(0);
        public IReadOnlyList<Type> CodeTypes { get; } = Array.AsReadOnly(new[] { typeof(GridGameModule), typeof(GridGameRules) });
        public IReadOnlyList<string> NativeLibraries => Array.Empty<string>();
        public JsonElement Description => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", Id);
            writer.WriteNumber("version", Version);
            writer.WriteString("timing", "manual");
            writer.WriteStartArray("commands");
            writer.WriteStringValue("Move(side, unitId, direction: Up|Down|Left|Right)");
            writer.WriteStringValue("Attack(side, unitId, targetId)");
            writer.WriteStringValue("EndTurn(side)");
            writer.WriteEndArray();
            writer.WriteString("observation", "units, activeSide, actionPoints, turnIndex, winner");
            writer.WriteEndObject();
        });

        public JsonElement ReadConfiguration(JsonElement value)
        {
            ScenarioDefinition definition = GridData.ReadConfiguration(value, "$.configuration", StringComparison.OrdinalIgnoreCase);
            try
            {
                using GridGameRules validated = GridGameRules.Create(definition);
            }
            catch (ArgumentException error) { throw new ProtocolException("InvalidConfiguration", error.Message, "$.configuration", error); }
            return JsonData.Write(writer => GridData.WriteConfiguration(writer, definition));
        }

        public JsonElement ReadCommand(JsonElement value)
        {
            GridCommand command = GridData.ReadCommand(value, "$.command");
            return JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", command.GetType().Name);
                writer.WriteString("side", command.Side.ToString());
                if (command is Move move)
                {
                    writer.WriteNumber("unitId", move.UnitId);
                    writer.WriteString("direction", move.Direction.ToString());
                }
                else if (command is Attack attack)
                {
                    writer.WriteNumber("unitId", attack.UnitId);
                    writer.WriteNumber("targetId", attack.TargetId);
                }
                writer.WriteEndObject();
            });
        }

        public JsonElement ReadState(JsonElement configuration, JsonElement state, long processed)
        {
            try
            {
                GridGameState data = GridData.ReadGame(state, GridData.ReadConfiguration(configuration, "$.configuration"));
                using GridGameRules validated = GridGameRules.Restore(data);
                JsonData.Require(data.TurnIndex - 1 <= processed, "InvalidState", "$.state.turnIndex");
                return JsonData.Write(writer => GridData.WriteGame(writer, data));
            }
            catch (ArgumentException error) { throw new ProtocolException("InvalidState", error.Message, "$.state", error); }
        }

        public void ValidateResult(JsonElement result)
        {
            foreach (JsonElement input in result.GetProperty("inputs").EnumerateArray())
            {
                if (!input.GetProperty("accepted").GetBoolean())
                {
                    GridData.ValidateError(input.GetProperty("error").GetString()!);
                }
            }
            int index = 0;
            foreach (JsonElement item in ProtocolJson.Array(result.GetProperty("events"), "$.events"))
            {
                GridData.ReadEvent(item, "$.events[" + index++ + "]");
            }
        }

        public IHostedGame Create(JsonElement configuration, ulong seed)
        {
            return CreatePlay(configuration, seed);
        }

        public GridHost CreatePlay(JsonElement configuration, ulong seed) => new GridHost(CreateRuntime(configuration, seed));
        public GridCommand DecodePlayCommand(JsonElement value) => GridData.ReadCommand(value, "$.command");

        /// <summary>创建一个对局和它的顺序表现队列。每次 Advance 推送同一 Tick 的隔离观察，宿主按自己的节奏消费。</summary>
        public IHostedGame CreateWithPresentation(JsonElement configuration, ulong seed, out SequentialPresentationQueue<GridGameView> frames)
        {
            var runtime = CreateRuntime(configuration, seed);
            try
            {
                frames = new SequentialPresentationQueue<GridGameView>(runtime.Capture());
                return new GridHost(runtime, frames);
            }
            catch
            {
                runtime.Dispose();
                throw;
            }
        }

        private SimulationRuntime<GridCommand, GridGameView, GridGameState> CreateRuntime(JsonElement configuration, ulong seed)
        {
            ScenarioDefinition definition = GridData.ReadConfiguration(ReadConfiguration(configuration), "$.configuration");
            return SimulationRuntime<GridCommand, GridGameView, GridGameState>.Create(() => GridGameRules.Create(definition), seed, Timing);
        }

        public IHostedGame Restore(JsonElement configuration, JsonElement state, RuntimeState runtime)
        {
            state = ReadState(configuration, state, runtime.Processed);
            JsonData.Require(runtime.Timing.TicksPerSecond == 0, "TimingMismatch", "$.runtime.ticksPerSecond");
            GridGameState data = GridData.ReadGame(state, GridData.ReadConfiguration(configuration, "$.configuration"));
            return new GridHost(SimulationRuntime<GridCommand, GridGameView, GridGameState>.Restore(
                new SimulationSnapshot<GridGameState>(runtime, data), GridGameRules.Restore));
        }
    }

    public sealed class GridHost : HostedGame<GridCommand, GridGameView, GridGameState>
    {
        internal GridHost(SimulationRuntime<GridCommand, GridGameView, GridGameState> runtime, SequentialPresentationQueue<GridGameView>? frames = null)
            : base(runtime, frames) { }
        protected override GridCommand ReadCommand(JsonElement command) => GridData.ReadCommand(command, "$.command");
        protected override void WriteState(Utf8JsonWriter writer, GridGameState state) => GridData.WriteGame(writer, state);
        protected override void WriteEvent(Utf8JsonWriter writer, GameEvent value) => GridData.WriteEvent(writer, value);
        protected override void WriteObservation(Utf8JsonWriter writer, GridGameView view)
        {
            writer.WriteStartObject();
            writer.WriteStartArray("units");
            foreach (UnitDefinition unit in view.Units)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", unit.Id);
                writer.WriteString("side", unit.Side.ToString());
                writer.WriteNumber("x", unit.Position.X);
                writer.WriteNumber("y", unit.Position.Y);
                writer.WriteNumber("health", unit.Health);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("activeSide", view.ActiveSide.ToString());
            writer.WriteNumber("actionPoints", view.ActionPoints);
            writer.WriteString("turnIndex", StateCodec.Text(view.TurnIndex));
            if (view.Winner.HasValue)
            {
                writer.WriteString("winner", view.Winner.Value.ToString());
            }
            else
            {
                writer.WriteNull("winner");
            }
            writer.WriteEndObject();
        }
    }
}
