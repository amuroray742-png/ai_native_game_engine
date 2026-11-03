using System;

namespace AiNative.GridGame.Hosting
{
    /// <summary>由宿主提供的来源信息；共享库不读取操作系统、文件或程序集位置。</summary>
    public sealed class HostEnvironment
    {
        public HostEnvironment(string runtimeVersion, string os, string architecture, string buildHash)
        {
            RuntimeVersion = RequireText(runtimeVersion, nameof(runtimeVersion));
            Os = RequireText(os, nameof(os));
            Architecture = RequireText(architecture, nameof(architecture));
            BuildHash = RequireText(buildHash, nameof(buildHash));
            if (buildHash.Length != 64)
            {
                throw new ArgumentException("构建摘要必须为 64 位小写十六进制。", nameof(buildHash));
            }
            foreach (char value in buildHash)
            {
                if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f')))
                {
                    throw new ArgumentException("构建摘要必须为 64 位小写十六进制。", nameof(buildHash));
                }
            }
        }

        public string RuntimeVersion { get; }
        public string Os { get; }
        public string Architecture { get; }
        public string BuildHash { get; }

        private static string RequireText(string value, string name) =>
            value ?? throw new ArgumentNullException(name);
    }
}
