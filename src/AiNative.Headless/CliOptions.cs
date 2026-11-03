using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiNative.Headless
{
    internal sealed class CliOptions
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private CliOptions(string mode)
        {
            Mode = mode;
        }
        internal string Mode { get; }
        internal ulong Seed { get; private set; }
        internal string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;
        internal string Required(string key) => Get(key) ?? throw Invalid("缺少选项：" + key);

        internal static CliOptions Parse(string[] args)
        {
            if (args.Length == 0 || (args.Length == 1 && args[0] == "--help"))
            {
                return new CliOptions("help");
            }
            var options = new CliOptions(args[0]);
            string[] allowed = options.Mode switch
            {
                "games" => Array.Empty<string>(),
                "describe" => new[] { "--game" },
                "compare" => new[] { "--expected", "--actual" },
                "inspect" => new[] { "--snapshot", "--expect-config" },
                "replay" => new[] { "--input", "--expect-config" },
                "run" => new[] { "--scenario", "--snapshot", "--seed", "--commands", "--expect-config", "--snapshot-out", "--replay-out" },
                _ => throw Invalid("未知子命令。")
            };
            if (args.Length == 2 && args[1] == "--help")
            {
                return new CliOptions("help");
            }
            for (int index = 1; index < args.Length; index += 2)
            {
                if (!allowed.Contains(args[index], StringComparer.Ordinal) || index + 1 == args.Length
                    || string.IsNullOrEmpty(args[index + 1]) || !options._values.TryAdd(args[index], args[index + 1]))
                {
                    throw Invalid("未知、重复或缺少值的选项：" + args[index]);
                }
            }
            switch (options.Mode)
            {
                case "describe":
                    options.Required("--game");
                    break;
                case "compare":
                    options.Required("--expected");
                    options.Required("--actual");
                    break;
                case "inspect":
                    options.Required("--snapshot");
                    break;
                case "replay":
                    options.Required("--input");
                    break;
                case "run":
                    options.Required("--commands");
                    bool scene = options.Get("--scenario") != null, snapshot = options.Get("--snapshot") != null;
                    if (scene == snapshot || (snapshot && options.Get("--seed") != null) || (scene && options.Get("--expect-config") != null))
                    {
                        throw Invalid("scenario + seed 与 snapshot 二选一；expect-config 仅用于恢复。");
                    }
                    if (scene)
                    {
                        string seed = options.Required("--seed");
                        if (seed.Length == 0 || (seed.Length > 1 && seed[0] == '0')
                            || seed.Any(value => value < '0' || value > '9')
                            || !ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
                        {
                            throw Invalid("seed 必须是无前导零的 UInt64 十进制整数。");
                        }
                        options.Seed = parsed;
                    }
                    break;
            }
            return options;
        }
        private static HostOperationException Invalid(string message) => new HostOperationException("InputError", "InvalidArguments", message);
    }
}
