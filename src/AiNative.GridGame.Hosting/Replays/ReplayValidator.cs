using System;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.GridGame.Hosting
{
    internal static class ReplayValidator
    {
        // 只推导调度，不执行玩法，也不根据命令重建或修正期望结果。
        internal static void Validate(ReplayData replay)
        {
            GameCheckpoint initial = replay.Initial.Value;
            ReplayReader.Require(initial.OperationIndex == null, "$.initialCheckpoint.operationIndex");
            ReplayReader.Require(initial.Submitted == replay.Snapshot.Submitted && initial.Completed == replay.Snapshot.Completed
                && initial.Pending == 0, "$.initialCheckpoint.session");
            ValidateState(replay.Initial, replay.Snapshot.ConfigHash, "$.initialCheckpoint");
            ReplayReader.Require(initial.StateHash == replay.Snapshot.StateHash, "$.initialCheckpoint.state");
            using (JsonDocument result = JsonDocument.Parse(initial.Result))
            {
                ReplayReader.Require(result.RootElement.GetProperty("kind").GetString() == "initial", "$.initialCheckpoint.result.kind");
            }
            ReplayReader.Require(replay.Checkpoints.Count == replay.Commands.Operations.Count, "$.checkpoints");
            long submitted = initial.Submitted;
            long completed = initial.Completed;
            int pending = 0;
            string previousHash = initial.StateHash;
            for (int index = 0; index < replay.Checkpoints.Count; index++)
            {
                ReplayCheckpoint checkpoint = replay.Checkpoints[index];
                string path = $"$.checkpoints[{index}]";
                GameCheckpoint value = checkpoint.Value;
                ReplayReader.Require(value.OperationIndex == index, path + ".operationIndex");
                string kind;
                long? sequence = null;
                if (replay.Commands.Operations[index].Command != null)
                {
                    ReplayReader.Require(submitted < long.MaxValue && pending < int.MaxValue, path + ".session");
                    sequence = ++submitted;
                    pending++;
                    kind = "submitted";
                }
                else if (pending != 0)
                {
                    sequence = ++completed;
                    pending--;
                    kind = "executed";
                }
                else
                {
                    kind = "empty";
                }
                ReplayReader.Require(value.Submitted == submitted && value.Completed == completed && value.Pending == pending, path + ".session");
                ValidateState(checkpoint, replay.Snapshot.ConfigHash, path);
                if (kind == "submitted" || kind == "empty")
                {
                    ReplayReader.Require(value.StateHash == previousHash, path + ".state");
                }
                using JsonDocument result = JsonDocument.Parse(value.Result);
                ReplayReader.Require(result.RootElement.GetProperty("kind").GetString() == kind, path + ".result.kind");
                if (sequence.HasValue)
                {
                    ReplayReader.Require(result.RootElement.GetProperty("sequence").GetString() == sequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), path + ".result.sequence");
                }
                previousHash = value.StateHash;
            }
            // 队列平衡在检查点自身一致性之后检查，沿用命令文件错误语义。
            CommandFileReader.ValidateBalance(replay.Commands, "$.commandFile.operations");
        }

        private static void ValidateState(ReplayCheckpoint checkpoint, string configHash, string path)
        {
            ReplayReader.Require(checkpoint.DigestVersion == 1, path + ".state.digestVersion");
            ReplayReader.Require(checkpoint.RulesId == "grid-game", path + ".state.rulesId");
            ReplayReader.Require(checkpoint.RulesVersion == 1, path + ".state.rulesVersion");
            ReplayReader.Require(checkpoint.ConfigHash == configHash, path + ".state.configHash");
            ReplayReader.Require(checkpoint.Algorithm == DeterministicRandom.Algorithm, path + ".state.random.algorithm");
            try
            {
                GridGameRules.Restore(checkpoint.Game);
            }
            catch (ArgumentException error)
            {
                throw new ProtocolException("InvalidReplay", error.Message, path + ".state.game", error);
            }
            ReplayReader.Require(checkpoint.Game.TurnIndex - 1 <= checkpoint.Value.Completed, path + ".state.game.turnIndex");
            string hash = CanonicalState.Hash(CanonicalState.StateBytes(checkpoint.Game, checkpoint.Algorithm, checkpoint.RandomState));
            ReplayReader.Require(hash == checkpoint.Value.StateHash, path + ".stateHash");
        }
    }
}
