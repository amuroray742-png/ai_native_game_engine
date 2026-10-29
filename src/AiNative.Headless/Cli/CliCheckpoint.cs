using System;
using System.Globalization;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Headless
{
    internal sealed record CliCheckpoint(int? OperationIndex, long Submitted, long Completed, int Pending,
        byte[] State, string StateHash, byte[] Result)
    {
        internal static CliCheckpoint Capture(int? index, RestoredGame game, byte[] result)
        {
            // 查询会经过 Session 故障/重入边界，再导出归该会话独占的玩法数据。
            game.Session.GetState();
            byte[] state = CanonicalState.StateBytes(game.Rules.ExportState(), DeterministicRandom.Algorithm, game.Session.RandomState);
            return new CliCheckpoint(index, game.Session.LastSubmittedSequence, game.Session.CompletedStepCount,
                game.Session.PendingCount, state, CanonicalState.Hash(state), result);
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
                    throw new HostOperationException("HostFailure", "UnexpectedHostFailure", "无法编码未知事件类型。");
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
