using System;
using System.Collections.Generic;
using AiNative.GridGame.Hosting;

namespace AiNative.Headless
{
    internal static class HostComparisonCommand
    {
        internal static void Execute(string[] args, CliResponse response)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index += 2)
            {
                string key = args[index];
                if ((key != "--expected" && key != "--actual") || index + 1 >= args.Length
                    || string.IsNullOrEmpty(args[index + 1]) || !paths.TryAdd(key, args[index + 1]))
                {
                    throw Invalid();
                }
            }
            if (!paths.TryGetValue("--expected", out string? expected) || !paths.TryGetValue("--actual", out string? actual))
            {
                throw Invalid();
            }
            byte[] expectedBytes = JsonInput.ReadFile(expected);
            byte[] actualBytes = JsonInput.ReadFile(actual);
            HostComparison comparison = HostVerification.Compare(expectedBytes, actualBytes);
            response.ComparedCheckpoints = comparison.ComparedCheckpoints;
            response.OperationIndex = comparison.OperationIndex;
            response.Sequence = comparison.Sequence;
            response.Divergence = comparison.Divergence;
            if (!comparison.Matched)
            {
                throw new HostOperationException("ReplayDivergence", "CheckpointMismatch", "两份宿主记录存在结果分歧。",
                    comparison.Differences[0].Path);
            }
            response.Code = "HostTracesMatched";
        }

        private static HostOperationException Invalid() => new HostOperationException("InputError", "InvalidArguments",
            "compare 要求 --expected <replay> 和 --actual <replay>，不接受其他或重复选项。");
    }
}
