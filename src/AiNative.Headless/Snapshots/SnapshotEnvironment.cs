using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Headless
{
    internal sealed record SnapshotEnvironment(string RuntimeVersion, string Os, string Architecture, string BuildHash)
    {
        internal static SnapshotEnvironment Current()
        {
            try
            {
                string os = OperatingSystem.IsWindows() ? "windows"
                    : OperatingSystem.IsLinux() ? "linux"
                    : OperatingSystem.IsMacOS() ? "macos"
                    : throw new PlatformNotSupportedException("快照不支持此操作系统。");
                string core = CanonicalState.Hash(File.ReadAllBytes(typeof(SessionState).Assembly.Location));
                string grid = CanonicalState.Hash(File.ReadAllBytes(typeof(GridGameRules).Assembly.Location));
                string headless = CanonicalState.Hash(File.ReadAllBytes(typeof(SnapshotEnvironment).Assembly.Location));
                return new SnapshotEnvironment(Environment.Version.ToString(), os,
                    RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    CanonicalState.Hash(BuildIdentityBytes(core, grid, headless)));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                throw new HostOperationException("HostFailure", "BuildIdentityUnavailable", "无法确定当前运行环境及构建身份。", inner: error);
            }
        }

        internal static byte[] BuildIdentityBytes(string coreHash, string gridHash, string headlessHash) =>
            Encoding.UTF8.GetBytes($"AiNative.Core={coreHash}\nAiNative.GridGame={gridHash}\nAiNative.Headless={headlessHash}\n");
    }
}
