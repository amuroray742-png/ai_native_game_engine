using System;
using System.Text.Json;

namespace AiNative.GridGame.Hosting
{
    /// <summary>所有宿主共用的场景格式读取；不读取文件，领域约束由 GridGameRuntime.Create 校验。</summary>
    public static class ScenarioCodec
    {
        /// <summary>严格解析场景 v1；阵营仅允许 A/B 的大小写形式，失败抛 ProtocolException。</summary>
        public static ScenarioDefinition Decode(ReadOnlyMemory<byte> bytes)
        {
            (int version, ScenarioDefinition definition) = Read(bytes);
            if (version != 1)
            {
                throw new ProtocolException("UnsupportedVersion", "不支持的场景版本。", "$.formatVersion");
            }
            return definition;
        }

        // CLI 先读取全部结构再检查版本，以保持已有多错误优先级。
        internal static (int Version, ScenarioDefinition Definition) Read(ReadOnlyMemory<byte> bytes)
        {
            using JsonDocument document = JsonDocumentReader.Parse(bytes);
            return SnapshotReader.ReadConfiguration(document.RootElement, "$", StringComparison.OrdinalIgnoreCase);
        }
    }
}
