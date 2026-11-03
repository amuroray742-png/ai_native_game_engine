using System;
using System.IO;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;

namespace AiNative.Headless
{
    internal static class CliRunner
    {
        private const string Help = "AiNative.Headless [scenario.json]\n"
            + "AiNative.Headless run --scenario <path> --seed <uint64> --commands <path> [--snapshot-out <path>] [--replay-out <path>]\n"
            + "AiNative.Headless run --snapshot <path> --commands <path> [--expect-config <path>] [--snapshot-out <path>] [--replay-out <path>]\n"
            + "AiNative.Headless inspect --snapshot <path> [--expect-config <path>]\n"
            + "AiNative.Headless replay --input <path> [--expect-config <path>]\n"
            + "AiNative.Headless compare --expected <replay> --actual <replay>\n"
            + "--help 或 <run|inspect|replay|compare> --help：显示帮助。\n";

        internal static int Run(string[] args, TextWriter output, TextWriter error)
        {
            if (CliOptions.IsHelp(args))
            {
                return WriteOutput(output, error, Help, 0);
            }
            var response = new CliResponse();
            try
            {
                if (args[0] == "compare")
                {
                    HostComparisonCommand.Execute(args, response);
                }
                else
                {
                    Execute(args, response);
                }
            }
            catch (ProtocolException failure)
            {
                response.Fail(new HostOperationException("InputError", failure.Code, failure.Message, failure.Path, failure));
            }
            catch (HostOperationException failure)
            {
                response.Fail(failure);
            }
            catch (Exception failure)
            {
                response.Fail(new HostOperationException("HostFailure", "UnexpectedHostFailure", failure.Message, inner: failure));
            }
            if (response.ExitCode != 0)
            {
                Diagnose(error, $"{response.Category}/{response.Code}: {response.Message}");
            }
            string json;
            try
            {
                json = response.Encode() + "\n";
            }
            catch (Exception failure)
            {
                Diagnose(error, $"HostFailure/UnexpectedHostFailure: {failure.Message}");
                return 1;
            }
            return WriteOutput(output, error, json, response.ExitCode);
        }

        private static void Execute(string[] args, CliResponse response)
        {
            CliOptions options = CliOptions.Parse(args);
            PreparedRun prepared = CliInput.Prepare(options);
            response.Initial = Capture(null, prepared.Game, OperationOutput.Initial());
            byte[]? initialSnapshot = options.ReplayOutput == null ? null
                : SnapshotCodec.Encode(prepared.Game.ExportState(), prepared.Environment);
            response.Code = options.Mode switch
            {
                "inspect" => "SnapshotInspected",
                "replay" => "ReplayMatched",
                _ => "RunCompleted"
            };
            if (prepared.Replay != null)
            {
                Compare(response, prepared.Replay.Initial.Value, response.Initial);
            }
            for (int index = 0; index < prepared.Commands.Operations.Count; index++)
            {
                CommandOperation operation = prepared.Commands.Operations[index];
                response.OperationIndex = index;
                response.Sequence = operation.Command == null && prepared.Game.PendingCount > 0
                    ? prepared.Game.CompletedStepCount + 1 : null;
                long? submitted = null;
                StepResult? step = null;
                try
                {
                    if (operation.Command != null)
                    {
                        submitted = prepared.Game.Submit(operation.Command);
                        response.Sequence = submitted;
                    }
                    else
                    {
                        step = prepared.Game.Advance()?.Step;
                    }
                }
                catch (OverflowException failure) when (operation.Command != null && !prepared.Game.IsFaulted)
                {
                    throw new HostOperationException("SimulationFailure", "SequenceOverflow", failure.Message, inner: failure);
                }
                catch (Exception failure)
                {
                    throw new HostOperationException("SimulationFailure", "SessionFaulted", failure.Message, inner: failure);
                }
                if (step != null)
                {
                    if (step.Result.Succeeded)
                    {
                        response.Accepted++;
                    }
                    else
                    {
                        response.Rejected++;
                    }
                }
                byte[] result;
                if (submitted.HasValue)
                {
                    result = OperationOutput.Submitted(submitted.Value);
                }
                else if (step == null)
                {
                    result = OperationOutput.Empty();
                }
                else
                {
                    result = OperationOutput.Executed(step);
                }
                GameCheckpoint actual = Capture(index, prepared.Game, result);
                response.Checkpoints.Add(actual);
                if (prepared.Replay != null)
                {
                    Compare(response, prepared.Replay.Checkpoints[index].Value, actual);
                }
            }
            response.OperationIndex = null;
            response.Sequence = null;
            if (prepared.Game.PendingCount != 0)
            {
                throw new HostOperationException("InputError", "PendingCommands", "执行结束仍有待执行命令。", "$.operations");
            }
            if (options.SnapshotOutput != null)
            {
                string path = new SnapshotStore(prepared.Environment).Save(options.SnapshotOutput, prepared.Game);
                response.SnapshotArtifacts.Add(path);
            }
            if (options.ReplayOutput != null)
            {
                byte[] bytes = ReplayWriter.Encode(initialSnapshot ?? throw new InvalidOperationException(),
                    prepared.CommandBytes ?? throw new InvalidOperationException(), response.Initial, response.Checkpoints);
                string path = AtomicFilePublisher.Publish(options.ReplayOutput, stream => stream.Write(bytes));
                response.ReplayArtifacts.Add(path);
            }
        }

        private static void Compare(CliResponse response, GameCheckpoint expected, GameCheckpoint actual)
        {
            response.Divergence = CheckpointDifference.Compare(expected, actual);
            if (response.Divergence != null)
            {
                throw new HostOperationException("ReplayDivergence", "CheckpointMismatch", "实际执行与期望检查点不符。",
                    response.Divergence.Differences[0].Path);
            }
            response.ComparedCheckpoints++;
        }

        private static GameCheckpoint Capture(int? index, GridGameRuntime game, byte[] result)
        {
            try
            {
                return GameCheckpoint.Capture(index, game, result);
            }
            catch (Exception failure) when (game.IsFaulted)
            {
                throw new HostOperationException("SimulationFailure", "SessionFaulted", failure.Message, inner: failure);
            }
        }

        private static int WriteOutput(TextWriter output, TextWriter error, string text, int exitCode)
        {
            try
            {
                output.Write(text);
                output.Flush();
                return exitCode;
            }
            catch (Exception failure)
            {
                // 写入已开始后不能补第二个 JSON；发布成功的文件也不回滚。
                Diagnose(error, $"HostFailure/StandardOutputFailed: {failure.Message}");
                return 1;
            }
        }

        private static void Diagnose(TextWriter error, string message)
        {
            try
            {
                error.WriteLine(message);
                error.Flush();
            }
            catch (Exception)
            {
                // 诊断流故障不能覆盖已确定的进程退出语义。
            }
        }
    }
}
