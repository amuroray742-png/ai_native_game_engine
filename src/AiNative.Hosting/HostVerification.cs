using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiNative.Hosting
{
    public sealed class RecordedRun
    {
        internal RecordedRun(JsonElement replay, JsonElement snapshot, JsonElement observation, int accepted, int rejected)
        {
            Replay = replay;
            Snapshot = snapshot;
            Observation = observation;
            Accepted = accepted;
            Rejected = rejected;
        }
        public JsonElement Replay { get; }
        public JsonElement Snapshot { get; }
        public JsonElement Observation { get; }
        public int Accepted { get; }
        public int Rejected { get; }
    }

    public sealed class ReplayMismatchException : Exception
    {
        internal ReplayMismatchException(int operationIndex, IReadOnlyList<FieldDifference> differences, int accepted, int rejected)
            : base("回放在首个检查点分歧处停止。")
        {
            OperationIndex = operationIndex;
            Differences = differences;
            Accepted = accepted;
            Rejected = rejected;
        }
        public int OperationIndex { get; }
        public IReadOnlyList<FieldDifference> Differences { get; }
        public int Accepted { get; }
        public int Rejected { get; }
    }

    public sealed class FieldDifference
    {
        internal FieldDifference(string path, string expected, string actual)
        {
            Path = path;
            Expected = expected;
            Actual = actual;
        }
        public string Path { get; }
        public string Expected { get; }
        public string Actual { get; }
    }

    public sealed class HostComparison
    {
        internal HostComparison(HostEnvironment expected, HostEnvironment actual, int matched, int? operation, IReadOnlyList<FieldDifference> differences)
        {
            ExpectedEnvironment = expected;
            ActualEnvironment = actual;
            ComparedCheckpoints = matched;
            OperationIndex = operation;
            Differences = differences;
        }
        public HostEnvironment ExpectedEnvironment { get; }
        public HostEnvironment ActualEnvironment { get; }
        public int ComparedCheckpoints { get; }
        public int? OperationIndex { get; }
        public IReadOnlyList<FieldDifference> Differences { get; }
        public bool Matched => Differences.Count == 0;
    }

    public static class HostVerification
    {
        public static RecordedRun Record(IGameModule module, JsonElement configuration, IHostedGame game,
            CommandFile commands, HostEnvironment environment, ReplayRecord? expected = null,
            Action<JsonElement, int, int>? checkpointCompleted = null, Action<int>? operationStarting = null)
        {
            RecordedRun? run = null;
            foreach (JsonElement _ in RecordSteps(module, configuration, game, commands, environment,
                game.Submit, game.Advance, completed => run = completed, expected, checkpointCompleted, operationStarting))
            {
                // CLI 驱动到结束；Unity 验收每个渲染帧仅驱动一次。
            }
            return run ?? throw new InvalidOperationException("记录未完成。");
        }

        /// <summary>惰性执行给定宿主的输入入口，每次枚举只发布一个检查点。game 是同一局的借用协议视图。</summary>
        public static IEnumerable<JsonElement> RecordSteps(IGameModule module, JsonElement configuration, IHostedGame game,
            CommandFile commands, HostEnvironment environment, Func<JsonElement, long> submit, Func<JsonElement> advance,
            Action<RecordedRun> completed, ReplayRecord? expected = null, Action<JsonElement, int, int>? checkpointCompleted = null,
            Action<int>? operationStarting = null)
        {
            var recorder = new HostTraceRecorder(module, configuration, game, environment);
            JsonElement first = recorder.InitialCheckpoint;
            if (expected != null)
            {
                JsonData.Require(JsonData.Hash(expected.Commands.Json) == JsonData.Hash(commands.Json)
                    && expected.Initial.Module.Id == module.Id && JsonData.Hash(expected.InitialCheckpoint) == JsonData.Hash(first),
                    "ComparisonInputMismatch", "$.initialCheckpoint");
            }
            checkpointCompleted?.Invoke(first, 0, 0);
            JsonElement last = first;
            foreach (CommandOperation operation in commands.Operations)
            {
                yield return last;
                int index = recorder.Count;
                operationStarting?.Invoke(index);
                if (operation.Command.HasValue)
                {
                    JsonElement command = operation.Command.Value;
                    last = recorder.Submitted(command, submit(command));
                }
                else
                {
                    last = recorder.Advanced(advance());
                }
                checkpointCompleted?.Invoke(last, recorder.Accepted, recorder.Rejected);
                if (expected != null)
                {
                    var differences = Differences(expected.Checkpoints[index], last);
                    if (differences.Count != 0)
                    {
                        throw new ReplayMismatchException(index, differences, recorder.Accepted, recorder.Rejected);
                    }
                }
            }
            completed(recorder.Finish());
            yield return last;
        }

        public static HostComparison Compare(ReplayRecord expected, ReplayRecord actual)
        {
            JsonData.Require(expected.Initial.Module.Id == actual.Initial.Module.Id
                && expected.Initial.Module.Version == actual.Initial.Module.Version
                && expected.Initial.Json.GetProperty("configHash").GetString() == actual.Initial.Json.GetProperty("configHash").GetString()
                && expected.Initial.Json.GetProperty("stateHash").GetString() == actual.Initial.Json.GetProperty("stateHash").GetString()
                && JsonData.Hash(expected.Commands.Json) == JsonData.Hash(actual.Commands.Json), "ComparisonInputMismatch", "$");
            for (int index = 0; index < expected.Checkpoints.Count; index++)
            {
                IReadOnlyList<FieldDifference> differences = Differences(expected.Checkpoints[index], actual.Checkpoints[index]);
                if (differences.Count != 0)
                {
                    return new HostComparison(expected.Initial.Environment, actual.Initial.Environment, index + 1, index, differences);
                }
            }
            return new HostComparison(expected.Initial.Environment, actual.Initial.Environment,
                expected.Checkpoints.Count + 1, null, Array.Empty<FieldDifference>());
        }

        public static IReadOnlyList<FieldDifference> Differences(JsonElement expected, JsonElement actual)
        {
            var differences = new List<FieldDifference>();
            CompareValue(expected, actual, "$", differences);
            return differences.AsReadOnly();
        }

        private static void CompareValue(JsonElement expected, JsonElement actual, string path, List<FieldDifference> differences)
        {
            if (differences.Count >= 32)
            {
                return;
            }
            if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
            {
                var names = expected.EnumerateObject().Select(value => value.Name)
                    .Union(actual.EnumerateObject().Select(value => value.Name)).OrderBy(value => value, StringComparer.Ordinal);
                foreach (string name in names)
                {
                    bool left = expected.TryGetProperty(name, out JsonElement a), right = actual.TryGetProperty(name, out JsonElement b);
                    if (left && right)
                    {
                        CompareValue(a, b, path + "." + name, differences);
                    }
                    else
                    {
                        differences.Add(new FieldDifference(path + "." + name, left ? Preview(a) : "<missing>", right ? Preview(b) : "<missing>"));
                    }
                    if (differences.Count >= 32)
                    {
                        break;
                    }
                }
                return;
            }
            if (expected.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array
                && expected.GetArrayLength() == actual.GetArrayLength())
            {
                for (int index = 0; index < expected.GetArrayLength() && differences.Count < 32; index++)
                {
                    CompareValue(expected[index], actual[index], path + "[" + index + "]", differences);
                }
                return;
            }
            if (JsonData.Hash(expected) != JsonData.Hash(actual))
            {
                differences.Add(new FieldDifference(path, Preview(expected), Preview(actual)));
            }
        }

        private static string Preview(JsonElement value)
        {
            string text = value.GetRawText();
            return text.Length <= 256 ? text : text.Substring(0, 128) + "… sha256=" + JsonData.Hash(value);
        }

    }
}
