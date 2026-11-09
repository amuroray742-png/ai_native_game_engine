using System;
using System.Globalization;
using System.Text.Json;
using AiNative.Core;

namespace AiNative.Hosting
{
    /// <summary>通用运行时到工具协议的薄适配；具体类型的编解码由游戏提供。</summary>
    public abstract class HostedGame<TCommand, TObservation, TState> : IHostedGame, IPlaySession<TCommand, TObservation> where TCommand : class
    {
        private readonly SequentialPresentationQueue<TObservation>? _frames;

        protected HostedGame(SimulationRuntime<TCommand, TObservation, TState> runtime) : this(runtime, null) { }

        /// <summary>传入顺序队列时，Advance 完成结果编码后将同一 Tick 入队。表现消费由宿主驱动。</summary>
        protected HostedGame(SimulationRuntime<TCommand, TObservation, TState> runtime, SequentialPresentationQueue<TObservation>? frames)
        {
            Runtime = runtime;
            _frames = frames;
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

        public long Submit(TCommand command) => Runtime.Submit(command);
        public long Submit(JsonElement command) => Submit(ReadCommand(command));
        public Observation<TObservation> CaptureObservation() => Runtime.Capture();
        public JsonElement Observe() => Encode(writer => WriteObservation(writer, Runtime.GetState()));
        public JsonElement CaptureGameState() => Encode(writer => WriteState(writer, Runtime.CaptureGameState()));

        public TickResult<TObservation> AdvanceTick()
        {
            TickResult<TObservation> result = Runtime.Advance();
            Publish(result);
            return result;
        }

        public JsonElement Advance()
        {
            TickResult<TObservation> frame = Runtime.Advance();
            JsonElement encoded = EncodeTick(frame);
            Publish(frame);
            return encoded;
        }

        /// <summary>为记录器编码已完成的帧，不再次推进。</summary>
        public JsonElement EncodeTick(TickResult<TObservation> result) => Encode(writer =>
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

        private JsonElement Encode(Action<Utf8JsonWriter> write)
        {
            try
            { return JsonData.Write(write); }
            catch
            {
                Runtime.MarkFaulted();
                throw;
            }
        }

        private void Publish(TickResult<TObservation> frame)
        {
            try
            { _frames?.Enqueue(frame); }
            catch
            {
                Runtime.MarkFaulted();
                throw;
            }
        }

        public void Dispose() => Runtime.Dispose();
    }
}
