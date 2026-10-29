using System;
using System.Collections.Generic;
using System.Globalization;

namespace AiNative.Headless
{
    internal sealed record CliOptions(string Mode, string? Scenario, string? Snapshot, ulong? Seed,
        string? Commands, string? ExpectedConfiguration, string? SnapshotOutput, string? ReplayOutput, string? ReplayInput)
    {
        internal static bool IsHelp(string[] args) => (args.Length == 1 && args[0] == "--help")
            || (args.Length == 2 && (args[0] == "run" || args[0] == "inspect" || args[0] == "replay") && args[1] == "--help");

        internal static CliOptions Parse(string[] args)
        {
            if (args.Length == 0 || (args[0] != "run" && args[0] != "inspect" && args[0] != "replay"))
            {
                throw Invalid("需要 run、inspect 或 replay 子命令。");
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index += 2)
            {
                string key = args[index];
                bool allowed = key == "--expect-config" || (args[0] != "replay" && key == "--snapshot")
                    || (args[0] == "replay" && key == "--input")
                    || (args[0] == "run" && (key == "--scenario" || key == "--seed" || key == "--commands" || key == "--snapshot-out" || key == "--replay-out"));
                if (!allowed || index + 1 >= args.Length || string.IsNullOrEmpty(args[index + 1]) || !values.TryAdd(key, args[index + 1]))
                {
                    throw Invalid($"未知、重复或缺少值的选项：{key}。");
                }
            }
            string? Get(string key) => values.TryGetValue(key, out string? value) ? value : null;
            string? scenario = Get("--scenario");
            string? snapshot = Get("--snapshot");
            string? seedText = Get("--seed");
            ulong? seed = null;
            if (args[0] == "replay")
            {
                if (Get("--input") == null)
                {
                    throw Invalid("replay 要求 input。");
                }
                return new CliOptions("replay", null, null, null, null, Get("--expect-config"), null, null, Get("--input"));
            }
            if ((scenario == null) == (snapshot == null) || (scenario != null && seedText == null)
                || (snapshot != null && seedText != null) || (scenario != null && Get("--expect-config") != null)
                || (args[0] == "run" && Get("--commands") == null))
            {
                throw Invalid("run 要求场景和 seed 或快照二选一，并指定 commands；inspect 要求快照。");
            }
            if (seedText != null)
            {
                if ((seedText.Length > 1 && seedText[0] == '0')
                    || !ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
                {
                    throw Invalid("seed 必须为无前导零的 UInt64 十进制整数。");
                }
                seed = value;
            }
            return new CliOptions(args[0], scenario, snapshot, seed, Get("--commands"), Get("--expect-config"), Get("--snapshot-out"), Get("--replay-out"), null);
        }

        private static HostOperationException Invalid(string message) => new HostOperationException("InputError", "InvalidArguments", message);
    }
}
