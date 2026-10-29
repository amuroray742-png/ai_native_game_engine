using System;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Headless
{
    internal sealed record PreparedRun(RestoredGame Game, CommandFile Commands, SnapshotEnvironment Environment, ReplayData? Replay, byte[]? CommandBytes);

    internal static class CliInput
    {
        internal static PreparedRun Prepare(CliOptions options)
        {
            // 先按输入顺序读完文件，避免执行过程中重新读取变化的配置或命令。
            byte[] origin = JsonInput.ReadFile(options.Scenario ?? options.Snapshot ?? options.ReplayInput ?? throw new InvalidOperationException());
            byte[]? expectedBytes = options.ExpectedConfiguration == null ? null : JsonInput.ReadFile(options.ExpectedConfiguration);
            byte[]? commandBytes = options.Commands == null ? null : JsonInput.ReadFile(options.Commands);
            ReplayData? replay = null;
            SnapshotData? snapshot = null;
            (int Version, ScenarioDefinition Definition)? scene = null;
            if (options.Mode == "replay")
            {
                replay = ReplayReader.Read(origin);
                snapshot = replay.Snapshot;
            }
            else if (options.Scenario != null)
            {
                scene = ParseScene(origin);
            }
            else
            {
                snapshot = SnapshotReader.Read(origin);
            }
            (int Version, ScenarioDefinition Definition)? expected = expectedBytes == null ? null : ParseScene(expectedBytes);
            CommandFile commands = replay?.Commands ?? (commandBytes == null ? new CommandFile(1, Array.Empty<CommandOperation>()) : CommandFileReader.Read(commandBytes));
            // 所有结构校验之后再检查版本，随后才能装配规则。
            if (replay != null)
            {
                CheckVersion(replay.Version, "$.replayFormatVersion");
                ReplayReader.InSnapshot(() => { SnapshotCodec.ValidateVersions(replay.Snapshot); return true; });
                CheckVersion(commands.Version, "$.commandFile.commandFormatVersion");
            }
            else if (snapshot != null)
            {
                SnapshotCodec.ValidateVersions(snapshot);
            }
            else
            {
                CheckVersion(scene?.Version ?? throw new InvalidOperationException(), "$.formatVersion");
            }
            if (expected.HasValue)
            {
                CheckVersion(expected.Value.Version, "$.formatVersion");
            }
            CheckVersion(commands.Version, "$.commandFormatVersion");
            SnapshotEnvironment environment = SnapshotEnvironment.Current();
            RestoredGame game;
            if (snapshot != null)
            {
                game = replay == null ? SnapshotCodec.Decode(snapshot, environment, expected?.Definition)
                    : ReplayReader.InSnapshot(() => SnapshotCodec.Decode(snapshot, environment, expected?.Definition));
            }
            else
            {
                GridGameRules rules;
                try
                {
                    rules = GridGameRules.Create(scene?.Definition ?? throw new InvalidOperationException());
                }
                catch (ArgumentException error)
                {
                    throw new HostOperationException("InputError", "InvalidConfiguration", error.Message, "$.configuration", error);
                }
                game = new RestoredGame(rules, new SimulationSession<GridCommand, GridGameView>(rules,
                    options.Seed ?? throw new InvalidOperationException()));
            }
            if (replay != null)
            {
                ReplayValidator.Validate(replay);
            }
            else
            {
                CommandFileReader.ValidateBalance(commands);
            }
            string? snapshotDestination = null;
            if (options.SnapshotOutput != null)
            {
                snapshotDestination = AtomicFilePublisher.Preflight(options.SnapshotOutput);
            }
            if (options.ReplayOutput != null)
            {
                string replayDestination = AtomicFilePublisher.Preflight(options.ReplayOutput);
                StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (string.Equals(snapshotDestination, replayDestination, comparison))
                {
                    throw new HostOperationException("HostFailure", "OutputPathConflict", "快照与回放输出不能使用同一路径。", replayDestination);
                }
            }
            return new PreparedRun(game, commands, environment, replay, commandBytes);
        }

        private static (int Version, ScenarioDefinition Definition) ParseScene(byte[] bytes)
        {
            using JsonDocument document = JsonInput.Parse(bytes);
            try
            {
                var scene = ScenarioLoader.Parse(bytes);
                int index = 0;
                foreach (JsonElement unit in document.RootElement.GetProperty("units").EnumerateArray())
                {
                    string? side = unit.GetProperty("side").GetString();
                    if (!string.Equals(side, "A", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(side, "B", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new HostOperationException("InputError", "InvalidProtocol", "场景阵营必须为 A 或 B。", $"$.units[{index}].side");
                    }
                    index++;
                }
                return scene;
            }
            catch (JsonException error)
            {
                throw new HostOperationException("InputError", "InvalidProtocol", error.Message, error.Path ?? "$", error);
            }
        }

        private static void CheckVersion(int version, string path)
        {
            if (version != 1)
            {
                throw new HostOperationException("InputError", "UnsupportedVersion", "不支持的文件版本。", path);
            }
        }
    }
}
