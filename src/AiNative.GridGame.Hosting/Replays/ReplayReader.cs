using System;
using System.Collections.Generic;
using System.Text.Json;
using AiNative.GridGame;

namespace AiNative.GridGame.Hosting
{
    internal sealed class ReplayData
    {
        internal ReplayData(int version, SnapshotData snapshot, CommandFile commands, ReplayCheckpoint initial, IReadOnlyList<ReplayCheckpoint> checkpoints)
        {
            Version = version;
            Snapshot = snapshot;
            Commands = commands;
            Initial = initial;
            Checkpoints = checkpoints;
        }

        internal int Version { get; }
        internal SnapshotData Snapshot { get; }
        internal CommandFile Commands { get; }
        internal ReplayCheckpoint Initial { get; }
        internal IReadOnlyList<ReplayCheckpoint> Checkpoints { get; }
    }

    internal sealed class ReplayCheckpoint
    {
        internal ReplayCheckpoint(GameCheckpoint value, GridGameState game, int digestVersion, string rulesId, int rulesVersion, string configHash, string algorithm, ulong randomState)
        {
            Value = value;
            Game = game;
            DigestVersion = digestVersion;
            RulesId = rulesId;
            RulesVersion = rulesVersion;
            ConfigHash = configHash;
            Algorithm = algorithm;
            RandomState = randomState;
        }

        internal GameCheckpoint Value { get; }
        internal GridGameState Game { get; }
        internal int DigestVersion { get; }
        internal string RulesId { get; }
        internal int RulesVersion { get; }
        internal string ConfigHash { get; }
        internal string Algorithm { get; }
        internal ulong RandomState { get; }
    }

    internal static class ReplayReader
    {
        internal static ReplayData Read(ReadOnlyMemory<byte> bytes)
        {
            using JsonDocument document = JsonDocumentReader.Parse(bytes);
            JsonElement root = document.RootElement;
            ProtocolJson.Object(root, "$", "replayFormatVersion", "initialSnapshot", "commandFile", "initialCheckpoint", "checkpoints");
            int version = ProtocolJson.Int32(root.GetProperty("replayFormatVersion"), "$.replayFormatVersion");
            SnapshotData snapshot = InSnapshot(() => SnapshotReader.Read(root.GetProperty("initialSnapshot")));
            CommandFile commands;
            try
            {
                commands = CommandFileReader.Read(root.GetProperty("commandFile"));
            }
            catch (ProtocolException error)
            {
                throw Nested(error, "$.commandFile");
            }
            ReplayCheckpoint initial = ReadCheckpoint(root.GetProperty("initialCheckpoint"), snapshot.Game.Configuration, "$.initialCheckpoint");
            var checkpoints = new List<ReplayCheckpoint>();
            foreach (JsonElement item in ProtocolJson.Array(root.GetProperty("checkpoints"), "$.checkpoints"))
            {
                checkpoints.Add(ReadCheckpoint(item, snapshot.Game.Configuration, $"$.checkpoints[{checkpoints.Count}]"));
            }
            return new ReplayData(version, snapshot, commands, initial, checkpoints.AsReadOnly());
        }

        internal static T InSnapshot<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (ProtocolException error)
            {
                throw Nested(error, "$.initialSnapshot");
            }
        }

        private static ProtocolException Nested(ProtocolException error, string prefix) =>
            new ProtocolException(error.Code, error.Message,
                error.Path == null ? prefix : prefix + error.Path.Substring(1), error);

        private static ReplayCheckpoint ReadCheckpoint(JsonElement item, ScenarioDefinition configuration, string path)
        {
            try
            {
                ProtocolJson.Object(item, path, "operationIndex", "session", "state", "stateHash", "result");
                JsonElement index = item.GetProperty("operationIndex");
                int? operationIndex = index.ValueKind == JsonValueKind.Null ? null : ProtocolJson.Int32(index, path + ".operationIndex");
                JsonElement session = item.GetProperty("session");
                ProtocolJson.Object(session, path + ".session", "lastSubmittedSequence", "completedStepCount", "pendingCount");
                long submitted = ProtocolJson.Int64(session.GetProperty("lastSubmittedSequence"), path + ".session.lastSubmittedSequence");
                long completed = ProtocolJson.Int64(session.GetProperty("completedStepCount"), path + ".session.completedStepCount");
                int pending = ProtocolJson.Int32(session.GetProperty("pendingCount"), path + ".session.pendingCount");
                JsonElement state = item.GetProperty("state");
                string statePath = path + ".state";
                ProtocolJson.Object(state, statePath, "digestVersion", "rulesId", "rulesVersion", "configHash", "random", "game");
                int digest = ProtocolJson.Int32(state.GetProperty("digestVersion"), statePath + ".digestVersion");
                string rulesId = ProtocolJson.String(state.GetProperty("rulesId"), statePath + ".rulesId");
                int rulesVersion = ProtocolJson.Int32(state.GetProperty("rulesVersion"), statePath + ".rulesVersion");
                string configHash = ProtocolJson.Hash(state.GetProperty("configHash"), statePath + ".configHash");
                JsonElement random = state.GetProperty("random");
                ProtocolJson.Object(random, statePath + ".random", "algorithm", "state");
                string algorithm = ProtocolJson.String(random.GetProperty("algorithm"), statePath + ".random.algorithm");
                ulong randomState = ProtocolJson.UInt64(random.GetProperty("state"), statePath + ".random.state");
                GridGameState game = SnapshotReader.ReadGame(state.GetProperty("game"), configuration, statePath + ".game");
                string hash = ProtocolJson.Hash(item.GetProperty("stateHash"), path + ".stateHash");
                JsonElement result = item.GetProperty("result");
                ReadResult(result, path + ".result");
                var value = new GameCheckpoint(operationIndex, submitted, completed, pending,
                    CanonicalState.WriteBytes(state.WriteTo), hash, CanonicalState.WriteBytes(result.WriteTo));
                return new ReplayCheckpoint(value, game, digest, rulesId, rulesVersion, configHash, algorithm, randomState);
            }
            catch (ProtocolException error) when (error.Code == "InvalidProtocol")
            {
                throw new ProtocolException("InvalidReplay", error.Message, error.Path, error);
            }
        }

        private static void ReadResult(JsonElement result, string path)
        {
            // 先要求 kind，再根据联合分支校验精确字段集合。
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("kind", out JsonElement kindElement))
            {
                throw Invalid(path, "结果缺少 kind。");
            }
            string kind = ProtocolJson.String(kindElement, path + ".kind");
            if (kind == "initial" || kind == "empty")
            {
                ProtocolJson.Object(result, path, "kind");
                return;
            }
            if (kind == "submitted")
            {
                ProtocolJson.Object(result, path, "kind", "sequence");
                Require(ProtocolJson.Int64(result.GetProperty("sequence"), path + ".sequence") > 0, path + ".sequence");
                return;
            }
            Require(kind == "executed", path + ".kind");
            if (!result.TryGetProperty("outcome", out JsonElement outcomeElement))
            {
                throw Invalid(path + ".outcome", "缺少 outcome。");
            }
            string outcome = ProtocolJson.String(outcomeElement, path + ".outcome");
            Require(outcome == "Accepted" || outcome == "Rejected", path + ".outcome");
            if (outcome == "Rejected")
            {
                ProtocolJson.Object(result, path, "kind", "sequence", "outcome", "errorCode", "events");
                string code = ProtocolJson.String(result.GetProperty("errorCode"), path + ".errorCode");
                Require(Array.IndexOf(RuleErrors, code) >= 0, path + ".errorCode");
            }
            else
            {
                ProtocolJson.Object(result, path, "kind", "sequence", "outcome", "events");
            }
            Require(ProtocolJson.Int64(result.GetProperty("sequence"), path + ".sequence") > 0, path + ".sequence");
            int index = 0;
            foreach (JsonElement item in ProtocolJson.Array(result.GetProperty("events"), path + ".events"))
            {
                ReadEvent(item, $"{path}.events[{index++}]");
            }
            Require(outcome != "Rejected" || index == 0, path + ".events");
        }

        private static readonly string[] RuleErrors =
        {
            "GameFinished", "NotYourTurn", "UnknownUnit", "NotYourUnit", "InvalidDirection", "OutOfBounds",
            "Blocked", "Occupied", "NoActionPoints", "UnknownTarget", "FriendlyTarget", "NotAdjacent", "UnknownCommand"
        };

        private static void ReadEvent(JsonElement item, string path)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out JsonElement type))
            {
                throw Invalid(path, "事件缺少 type。");
            }
            switch (ProtocolJson.String(type, path + ".type"))
            {
                case "UnitMoved":
                    ProtocolJson.Object(item, path, "type", "unitId", "from", "to");
                    ProtocolJson.Int32(item.GetProperty("unitId"), path + ".unitId");
                    SnapshotReader.ReadPosition(item.GetProperty("from"), path + ".from");
                    SnapshotReader.ReadPosition(item.GetProperty("to"), path + ".to");
                    break;
                case "UnitDamaged":
                    ProtocolJson.Object(item, path, "type", "unitId", "damage", "health");
                    ProtocolJson.Int32(item.GetProperty("unitId"), path + ".unitId");
                    Require(ProtocolJson.Int32(item.GetProperty("damage"), path + ".damage") >= 0, path + ".damage");
                    Require(ProtocolJson.Int32(item.GetProperty("health"), path + ".health") >= 0, path + ".health");
                    break;
                case "TurnChanged":
                    ProtocolJson.Object(item, path, "type", "side", "turnIndex");
                    ProtocolJson.SideValue(item.GetProperty("side"), path + ".side");
                    Require(ProtocolJson.Int64(item.GetProperty("turnIndex"), path + ".turnIndex") > 0, path + ".turnIndex");
                    break;
                case "GameEnded":
                    ProtocolJson.Object(item, path, "type", "winner");
                    ProtocolJson.SideValue(item.GetProperty("winner"), path + ".winner");
                    break;
                default:
                    throw Invalid(path + ".type", "未知事件类型。");
            }
        }

        internal static void Require(bool condition, string path)
        {
            if (!condition)
            {
                throw Invalid(path, "回放检查点不满足协议或内部一致性约束。");
            }
        }

        private static ProtocolException Invalid(string path, string message) =>
            new ProtocolException("InvalidReplay", message, path);
    }
}
