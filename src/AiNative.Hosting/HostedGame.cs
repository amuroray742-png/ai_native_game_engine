using System;
using System.Globalization;
using System.Text.Json;
using AiNative.Core;

namespace AiNative.Hosting
{
    /// <summary>通用运行时到工具协议的薄适配；具体类型的编解码由游戏提供。</summary>
    public abstract class HostedGame<TCommand, TObservation, TState> : IHostedGame where TCommand : class
    {
        protected HostedGame(SimulationRuntime<TCommand, TObservation, TState> runtime)
        {
            Runtime = runtime;
        }
        protected SimulationRuntime<TCommand, TObservation, TState> Runtime { get; }
        public long Tick => Runtime.Tick;
        public long Submitted => Runtime.Submitted;
        public long Processed => Runtime.Processed;
        public int Pending => Runtime.PendingCount;
        public ulong RandomState => Runtime.RandomState;
        public SimulationTiming Timing => Runtime.Timing;

        protected abstract TCommand ReadCommand(JsonElement command);
        protected abstract void WriteObservation(Utf8JsonWriter writer, TObservation observation);
        protected abstract void WriteState(Utf8JsonWriter writer, TState state);
        protected abstract void WriteEvent(Utf8JsonWriter writer, GameEvent value);

        public long Submit(JsonElement command) => Runtime.Submit(ReadCommand(command));
        public JsonElement Observe() => JsonData.Write(writer => WriteObservation(writer, Runtime.GetState()));
        public JsonElement CaptureGameState() => JsonData.Write(writer => WriteState(writer, Runtime.CaptureGameState()));

        public JsonElement Advance()
        {
            TickResult<TObservation> result = Runtime.Advance();
            return JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "tick");
                writer.WriteString("tick", result.Tick.ToString(CultureInfo.InvariantCulture));
                writer.WriteStartArray("inputs");
                foreach (InputResult input in result.Inputs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sequence", input.Sequence.ToString(CultureInfo.InvariantCulture));
                    writer.WriteBoolean("accepted", input.Result.Succeeded);
                    if (input.Result.Error == null)
                    {
                        writer.WriteNull("error");
                    }
                    else
                    {
                        writer.WriteString("error", input.Result.Error);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("events");
                foreach (GameEvent value in result.Events)
                {
                    WriteEvent(writer, value);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        public void Dispose() => Runtime.Dispose();
    }
}
