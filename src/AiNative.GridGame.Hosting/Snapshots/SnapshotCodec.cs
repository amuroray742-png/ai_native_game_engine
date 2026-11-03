using System;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.GridGame.Hosting
{
    public static class SnapshotCodec
    {
        /// <summary>输入必须来自同一个会话的空队列边界；编码不访问文件或修改模拟。</summary>
        public static byte[] Encode(GridGameSnapshot snapshot, HostEnvironment environment)
        {
            if (snapshot == null || environment == null)
            {
                throw new ArgumentNullException(snapshot == null ? nameof(snapshot) : nameof(environment));
            }
            SessionState session = snapshot.Session;
            GridGameState game = snapshot.Game;
            ValidateConfiguration(game.Configuration);
            ValidateState(session.LastSubmittedSequence, session.CompletedStepCount, 0, session.RandomAlgorithm,
                session.RandomState, game);
            string configHash = CanonicalState.Hash(CanonicalState.ConfigurationBytes(game.Configuration));
            string stateHash = CanonicalState.Hash(CanonicalState.StateBytes(game, session.RandomAlgorithm, session.RandomState));
            return CanonicalState.WriteBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("snapshotFormatVersion", 1);
                writer.WriteString("rulesId", "grid-game");
                writer.WriteNumber("rulesVersion", 1);
                writer.WriteNumber("configFormatVersion", 1);
                writer.WritePropertyName("configuration");
                CanonicalState.WriteConfiguration(writer, game.Configuration);
                writer.WriteString("configHash", configHash);
                writer.WritePropertyName("session");
                CanonicalState.WriteSession(writer, session);
                writer.WritePropertyName("random");
                CanonicalState.WriteRandom(writer, session.RandomAlgorithm, session.RandomState);
                writer.WritePropertyName("game");
                CanonicalState.WriteGame(writer, game);
                writer.WriteNumber("digestVersion", 1);
                writer.WriteString("stateHash", stateHash);
                writer.WriteStartObject("environment");
                writer.WriteString("runtimeVersion", environment.RuntimeVersion);
                writer.WriteString("os", environment.Os);
                writer.WriteString("architecture", environment.Architecture);
                writer.WriteString("buildHash", environment.BuildHash);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }

        /// <summary>全部校验通过后才返回候选实例；调用方自行替换旧引用。</summary>
        public static GridGameRuntime Decode(ReadOnlyMemory<byte> bytes, HostEnvironment currentEnvironment,
            ScenarioDefinition? expectedConfiguration = null)
        {
            return Decode(SnapshotReader.Read(bytes), currentEnvironment, expectedConfiguration);
        }

        internal static void ValidateVersions(SnapshotData data)
        {
            Check(data.FormatVersion == 1, "UnsupportedVersion", "$.snapshotFormatVersion");
            Check(data.RulesId == "grid-game", "UnsupportedVersion", "$.rulesId");
            Check(data.RulesVersion == 1, "UnsupportedVersion", "$.rulesVersion");
            Check(data.ConfigFormatVersion == 1, "UnsupportedVersion", "$.configFormatVersion");
            Check(data.SceneFormatVersion == 1, "UnsupportedVersion", "$.configuration.formatVersion");
            Check(data.DigestVersion == 1, "UnsupportedVersion", "$.digestVersion");
            Check(data.Algorithm == DeterministicRandom.Algorithm, "UnsupportedRandomAlgorithm", "$.random.algorithm");
        }

        internal static GridGameRuntime Decode(SnapshotData data, HostEnvironment currentEnvironment,
            ScenarioDefinition? expectedConfiguration = null)
        {
            if (currentEnvironment == null)
            {
                throw new ArgumentNullException(nameof(currentEnvironment));
            }
            ValidateVersions(data);
            Check(data.Environment.RuntimeVersion == currentEnvironment.RuntimeVersion, "EnvironmentMismatch", "$.environment.runtimeVersion");
            Check(data.Environment.Os == currentEnvironment.Os, "EnvironmentMismatch", "$.environment.os");
            Check(data.Environment.Architecture == currentEnvironment.Architecture, "EnvironmentMismatch", "$.environment.architecture");
            Check(data.Environment.BuildHash == currentEnvironment.BuildHash, "EnvironmentMismatch", "$.environment.buildHash");
            return GridGameRuntime.Restore(ValidateContent(data, expectedConfiguration));
        }

        // 比较已有证据仅校验内容，不加载其来源环境、不执行命令、不安装一个对局。
        internal static GridGameSnapshot ValidateContent(SnapshotData data, ScenarioDefinition? expectedConfiguration = null)
        {
            ValidateVersions(data);
            ValidateConfiguration(data.Game.Configuration);
            string configHash = CanonicalState.Hash(CanonicalState.ConfigurationBytes(data.Game.Configuration));
            Check(configHash == data.ConfigHash, "ConfigHashMismatch", "$.configHash");
            if (expectedConfiguration != null)
            {
                ValidateConfiguration(expectedConfiguration);
                Check(CanonicalState.Hash(CanonicalState.ConfigurationBytes(expectedConfiguration)) == configHash,
                    "ExpectedConfigMismatch", "$.configuration");
            }
            GridGameSnapshot restored = ValidateState(data.Submitted, data.Completed, data.Pending, data.Algorithm, data.RandomState, data.Game);
            string restoredHash = CanonicalState.Hash(CanonicalState.StateBytes(restored.Game,
                DeterministicRandom.Algorithm, restored.Session.RandomState));
            Check(restoredHash == data.StateHash, "InvalidSnapshot", "$.stateHash");
            return restored;
        }

        private static void ValidateConfiguration(ScenarioDefinition configuration)
        {
            try
            {
                // 领域校验入口属于玩法模块；宿主不重复实现配置规则。
                GridGameRules.Create(configuration);
            }
            catch (ArgumentException error)
            {
                throw new ProtocolException("InvalidConfiguration", error.Message, "$.configuration", error);
            }
        }

        private static GridGameSnapshot ValidateState(long submitted, long completed, int pending,
            string algorithm, ulong randomState, GridGameState game)
        {
            Check(pending == 0, "InvalidSnapshot", "$.session.pendingCount");
            SessionState sessionState;
            try
            {
                sessionState = new SessionState(submitted, completed, algorithm, randomState);
            }
            catch (ArgumentException error)
            {
                throw new ProtocolException("InvalidSnapshot", error.Message, "$.session", error);
            }
            try
            {
                GridGameRules.Restore(game);
            }
            catch (ArgumentException error)
            {
                throw new ProtocolException("InvalidSnapshot", error.Message, "$.game", error);
            }
            // 玩法已保证 turnIndex >= 1，使用减法避免 completed + 1 溢出。
            Check(game.TurnIndex - 1 <= completed, "InvalidSnapshot", "$.game.turnIndex");
            return new GridGameSnapshot(sessionState, game);
        }

        private static void Check(bool condition, string code, string path)
        {
            if (!condition)
            {
                throw new ProtocolException(code, $"快照校验失败：{code}。", path);
            }
        }
    }

}
