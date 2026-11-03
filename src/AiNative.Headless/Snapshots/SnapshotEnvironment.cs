using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;

namespace AiNative.Headless
{
    internal static class SnapshotEnvironment
    {
        internal static HostEnvironment Current()
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
                return new HostEnvironment(Environment.Version.ToString(), os,
                    RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    CanonicalState.Hash(BuildIdentityBytes(new[]
                    {
                        ("AiNative.Core", core), ("AiNative.GridGame", grid),
                        ("AiNative.GridGame.Hosting", AssemblyHash(typeof(HostEnvironment))),
                        ("AiNative.Headless", headless),
                        ("System.Text.Json", AssemblyHash(typeof(System.Text.Json.JsonDocument))),
                        ("System.Text.Encodings.Web", AssemblyHash(typeof(System.Text.Encodings.Web.JavaScriptEncoder))),
                        ("System.IO.Pipelines", AssemblyHash(typeof(System.IO.Pipelines.PipeReader)))
                    })));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                throw new HostOperationException("HostFailure", "BuildIdentityUnavailable", "无法确定当前运行环境及构建身份。", inner: error);
            }
        }

        private static string AssemblyHash(Type type) => CanonicalState.Hash(File.ReadAllBytes(type.Assembly.Location));

        internal static byte[] BuildIdentityBytes(IReadOnlyList<(string Name, string Hash)> assemblies)
        {
            var text = new StringBuilder();
            foreach ((string name, string hash) in assemblies)
            {
                text.Append(name).Append('=').Append(hash).Append('\n');
            }
            return Encoding.UTF8.GetBytes(text.ToString());
        }
    }
}
