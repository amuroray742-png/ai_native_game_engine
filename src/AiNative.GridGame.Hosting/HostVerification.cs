using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.Core;

namespace AiNative.GridGame.Hosting
{
    /// <summary>宿主自动验收入口；来源环境用于记录，证据比较不授予跨环境恢复兼容性。</summary>
    public static class HostVerification
    {
        /// <summary>从相同场景、种子和完整命令文件独立运行，生成现有 v1 回放证据；不读取或发布文件。</summary>
        public static byte[] Record(ScenarioDefinition configuration, ulong seed,
            ReadOnlyMemory<byte> commandBytes, HostEnvironment environment)
        {
            byte[] commands = commandBytes.ToArray();
            CommandFile file = CommandFile.Decode(commands);
            GridGameRuntime game = GridGameRuntime.Create(configuration, seed);
            byte[] initialSnapshot = SnapshotCodec.Encode(game.ExportState(), environment);
            GameCheckpoint initial = GameCheckpoint.Capture(null, game, OperationOutput.Initial());
            var checkpoints = new List<GameCheckpoint>();
            for (int index = 0; index < file.Operations.Count; index++)
            {
                GridCommand? command = file.Operations[index].Command;
                byte[] result;
                if (command != null)
                {
                    result = OperationOutput.Submitted(game.Submit(command));
                }
                else
                {
                    GridStep? step = game.Advance();
                    result = step == null ? OperationOutput.Empty() : OperationOutput.Executed(step.Step);
                }
                checkpoints.Add(GameCheckpoint.Capture(index, game, result));
            }
            return ReplayWriter.Encode(initialSnapshot, commands, initial, checkpoints);
        }

        /// <summary>校验两份独立记录的内容和起点/操作一致性，再报告首个结果分歧；不执行记录中的命令。</summary>
        public static HostComparison Compare(ReadOnlyMemory<byte> expectedBytes, ReadOnlyMemory<byte> actualBytes)
        {
            ReplayData expected = ReadEvidence(expectedBytes);
            ReplayData actual = ReadEvidence(actualBytes);
            RequireSameInput(expected, actual);
            ReplayDivergence? divergence = CheckpointDifference.Compare(expected.Initial.Value, actual.Initial.Value);
            if (divergence != null)
            {
                return new HostComparison(expected.Snapshot.Environment, actual.Snapshot.Environment, 0, null, null, divergence);
            }
            int matched = 1;
            for (int index = 0; index < expected.Checkpoints.Count; index++)
            {
                GameCheckpoint checkpoint = actual.Checkpoints[index].Value;
                divergence = CheckpointDifference.Compare(expected.Checkpoints[index].Value, checkpoint);
                if (divergence != null)
                {
                    using JsonDocument result = JsonDocument.Parse(checkpoint.Result);
                    long? sequence = result.RootElement.TryGetProperty("sequence", out JsonElement value)
                        ? ProtocolJson.Int64(value, "$.result.sequence") : (long?)null;
                    return new HostComparison(expected.Snapshot.Environment, actual.Snapshot.Environment,
                        matched, index, sequence, divergence);
                }
                matched++;
            }
            return new HostComparison(expected.Snapshot.Environment, actual.Snapshot.Environment, matched, null, null, null);
        }

        private static ReplayData ReadEvidence(ReadOnlyMemory<byte> bytes)
        {
            ReplayData replay = ReplayReader.Read(bytes);
            if (replay.Version != 1 || replay.Commands.Version != 1)
            {
                throw new ProtocolException("UnsupportedVersion", "不支持的回放或命令版本。", "$");
            }
            ReplayReader.InSnapshot(() => SnapshotCodec.ValidateContent(replay.Snapshot));
            ReplayValidator.Validate(replay);
            return replay;
        }

        private static void RequireSameInput(ReplayData expected, ReplayData actual)
        {
            if (expected.Snapshot.ConfigHash != actual.Snapshot.ConfigHash
                || expected.Snapshot.StateHash != actual.Snapshot.StateHash
                || expected.Snapshot.Submitted != actual.Snapshot.Submitted
                || expected.Snapshot.Completed != actual.Snapshot.Completed)
            {
                throw DifferentInput("$.initialSnapshot");
            }
            if (expected.Commands.Operations.Count != actual.Commands.Operations.Count)
            {
                throw DifferentInput("$.commandFile.operations");
            }
            for (int index = 0; index < expected.Commands.Operations.Count; index++)
            {
                GridCommand? left = expected.Commands.Operations[index].Command;
                GridCommand? right = actual.Commands.Operations[index].Command;
                if (!SameCommand(left, right))
                {
                    throw DifferentInput($"$.commandFile.operations[{index}]");
                }
            }
        }

        private static bool SameCommand(GridCommand? left, GridCommand? right)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }
            if (left.Side != right.Side)
            {
                return false;
            }
            return left switch
            {
                Move move => right is Move other && move.UnitId == other.UnitId && move.Direction == other.Direction,
                Attack attack => right is Attack other && attack.UnitId == other.UnitId && attack.TargetId == other.TargetId,
                EndTurn _ => right is EndTurn,
                _ => false
            };
        }

        private static ProtocolException DifferentInput(string path) =>
            new ProtocolException("ComparisonInputMismatch", "两份记录必须具有相同配置、起始状态、调度位置和操作序列。", path);
    }

    /// <summary>两份证据的比较结果；Matched 仅覆盖本次输入，不表示两个环境普遍兼容。</summary>
    public sealed class HostComparison
    {
        internal HostComparison(HostEnvironment expectedEnvironment, HostEnvironment actualEnvironment,
            int comparedCheckpoints, int? operationIndex, long? sequence, ReplayDivergence? divergence)
        {
            ExpectedEnvironment = expectedEnvironment;
            ActualEnvironment = actualEnvironment;
            ComparedCheckpoints = comparedCheckpoints;
            OperationIndex = operationIndex;
            Sequence = sequence;
            Divergence = divergence;
        }

        public HostEnvironment ExpectedEnvironment { get; }
        public HostEnvironment ActualEnvironment { get; }
        public bool Matched => Divergence == null;
        public int ComparedCheckpoints { get; }
        public int? OperationIndex { get; }
        public long? Sequence { get; }
        public IReadOnlyList<FieldDifference> Differences => Divergence?.Differences ?? Array.Empty<FieldDifference>();
        internal ReplayDivergence? Divergence { get; }
    }
}
