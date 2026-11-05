using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AiNative.Hosting
{
    /// <summary>在宿主完成操作后立即取证，不推进借用的会话。每个记录器只服务一个连续操作区间。</summary>
    public sealed class HostTraceRecorder
    {
        private readonly IGameModule _module;
        private readonly JsonElement _configuration;
        private readonly IHostedGame _game;
        private readonly HostEnvironment _environment;
        private readonly JsonElement _initial;
        private readonly List<CommandOperation> _operations = new List<CommandOperation>();
        private readonly List<JsonElement> _checkpoints = new List<JsonElement>();

        public HostTraceRecorder(IGameModule module, JsonElement configuration, IHostedGame game, HostEnvironment environment)
        {
            _module = module;
            _configuration = configuration;
            _game = game;
            _environment = environment;
            _initial = StateCodec.Encode(module, configuration, game, environment);
            InitialCheckpoint = Capture(null, JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "initial");
                writer.WriteEndObject();
            }));
        }

        public JsonElement InitialCheckpoint { get; }
        public int Count => _checkpoints.Count;
        public int Accepted { get; private set; }
        public int Rejected { get; private set; }

        public JsonElement Submitted(JsonElement command, long sequence)
        {
            JsonElement result = JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "submitted");
                writer.WriteString("sequence", StateCodec.Text(sequence));
                writer.WriteEndObject();
            });
            return Append(new CommandOperation(_module.ReadCommand(command)), result);
        }

        public JsonElement Advanced(JsonElement result)
        {
            JsonElement checkpoint = Append(new CommandOperation(null), result);
            foreach (JsonElement input in result.GetProperty("inputs").EnumerateArray())
            {
                if (input.GetProperty("accepted").GetBoolean())
                {
                    Accepted++;
                }
                else
                {
                    Rejected++;
                }
            }
            return checkpoint;
        }

        private JsonElement Append(CommandOperation operation, JsonElement result)
        {
            JsonElement checkpoint = Capture(Count, result);
            JsonElement previous = Count == 0 ? InitialCheckpoint : _checkpoints[Count - 1];
            ReplayRecord.ValidateTransition(previous, checkpoint, operation, _module);
            _operations.Add(operation);
            _checkpoints.Add(checkpoint);
            return checkpoint;
        }

        public RecordedRun Finish()
        {
            JsonElement snapshot = StateCodec.Encode(_module, _configuration, _game, _environment);
            JsonElement last = Count == 0 ? InitialCheckpoint : _checkpoints[Count - 1];
            JsonData.Require(snapshot.GetProperty("stateHash").GetString() == last.GetProperty("stateHash").GetString(),
                "UnrecordedOperation", "$.runtime");
            JsonElement commands = CommandFile.Encode(_operations);
            JsonElement replay = JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 2);
                writer.WritePropertyName("initialSnapshot");
                _initial.WriteTo(writer);
                writer.WritePropertyName("commands");
                commands.WriteTo(writer);
                writer.WritePropertyName("initialCheckpoint");
                InitialCheckpoint.WriteTo(writer);
                writer.WriteStartArray("checkpoints");
                foreach (JsonElement checkpoint in _checkpoints)
                {
                    checkpoint.WriteTo(writer);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return new RecordedRun(replay, snapshot, _game.Observe(), Accepted, Rejected);
        }

        private JsonElement Capture(int? index, JsonElement result)
        {
            JsonElement runtime = StateCodec.Runtime(_game), state = _game.CaptureGameState();
            return JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                if (index.HasValue)
                {
                    writer.WriteNumber("operationIndex", index.Value);
                }
                else
                {
                    writer.WriteNull("operationIndex");
                }
                writer.WritePropertyName("runtime");
                runtime.WriteTo(writer);
                writer.WritePropertyName("state");
                state.WriteTo(writer);
                writer.WriteString("stateHash", StateCodec.Digest(_module, JsonData.Hash(_configuration), runtime, state));
                writer.WritePropertyName("result");
                result.WriteTo(writer);
                writer.WriteEndObject();
            });
        }
    }
}
