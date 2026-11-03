using System;
using System.Globalization;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.GridGame.Hosting
{
    internal sealed class GameCheckpoint
    {
        internal GameCheckpoint(int? operationIndex, long submitted, long completed, int pending, byte[] state, string stateHash, byte[] result)
        {
            OperationIndex = operationIndex;
            Submitted = submitted;
            Completed = completed;
            Pending = pending;
            State = state;
            StateHash = stateHash;
            Result = result;
        }

        internal int? OperationIndex { get; }
        internal long Submitted { get; }
        internal long Completed { get; }
        internal int Pending { get; }
        internal byte[] State { get; }
        internal string StateHash { get; }
        internal byte[] Result { get; }

        internal static GameCheckpoint Capture(int? index, GridGameRuntime game, byte[] result)
        {
            GridObservation observation = game.Capture();
            byte[] state = CanonicalState.StateBytes(observation.Game, DeterministicRandom.Algorithm, observation.RandomState);
            return new GameCheckpoint(index, observation.LastSubmittedSequence, observation.CompletedStepCount,
                observation.PendingCount, state, CanonicalState.Hash(state), result);
        }

        internal void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            if (OperationIndex.HasValue)
            {
                writer.WriteNumber("operationIndex", OperationIndex.Value);
            }
            else
            {
                writer.WriteNull("operationIndex");
            }
            writer.WriteStartObject("session");
            writer.WriteString("lastSubmittedSequence", Submitted.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("completedStepCount", Completed.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("pendingCount", Pending);
            writer.WriteEndObject();
            writer.WritePropertyName("state");
            writer.WriteRawValue(State);
            writer.WriteString("stateHash", StateHash);
            writer.WritePropertyName("result");
            writer.WriteRawValue(Result);
            writer.WriteEndObject();
        }
    }

    internal static class OperationOutput
    {
        internal static byte[] Initial() => Kind("initial");
        internal static byte[] Empty() => Kind("empty");
        private static byte[] Kind(string kind) => CanonicalState.WriteBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteEndObject();
        });

        internal static byte[] Submitted(long sequence) => CanonicalState.WriteBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "submitted");
            writer.WriteString("sequence", sequence.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        });

        internal static byte[] Executed(StepResult step) => CanonicalState.WriteBytes(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "executed");
            writer.WriteString("sequence", step.Sequence.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("outcome", step.Result.Succeeded ? "Accepted" : "Rejected");
            if (!step.Result.Succeeded)
            {
                writer.WriteString("errorCode", step.Result.Error);
            }
            writer.WriteStartArray("events");
            foreach (GameEvent value in step.Result.Events)
            {
                WriteEvent(writer, value);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        private static void WriteEvent(Utf8JsonWriter writer, GameEvent value)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case UnitMoved moved:
                    writer.WriteString("type", "UnitMoved");
                    writer.WriteNumber("unitId", moved.UnitId);
                    WritePosition(writer, "from", moved.From);
                    WritePosition(writer, "to", moved.To);
                    break;
                case UnitDamaged damaged:
                    writer.WriteString("type", "UnitDamaged");
                    writer.WriteNumber("unitId", damaged.UnitId);
                    writer.WriteNumber("damage", damaged.Damage);
                    writer.WriteNumber("health", damaged.Health);
                    break;
                case TurnChanged changed:
                    writer.WriteString("type", "TurnChanged");
                    writer.WriteString("side", changed.Side.ToString());
                    writer.WriteString("turnIndex", changed.TurnIndex.ToString(CultureInfo.InvariantCulture));
                    break;
                case GameEnded ended:
                    writer.WriteString("type", "GameEnded");
                    writer.WriteString("winner", ended.Winner.ToString());
                    break;
                default:
                    throw new InvalidOperationException("无法编码未知事件类型。");
            }
            writer.WriteEndObject();
        }

        private static void WritePosition(Utf8JsonWriter writer, string name, GridPosition position)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("x", position.X);
            writer.WriteNumber("y", position.Y);
            writer.WriteEndObject();
        }
    }
}
