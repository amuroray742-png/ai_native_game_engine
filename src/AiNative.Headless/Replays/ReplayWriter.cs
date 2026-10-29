using System.Collections.Generic;

namespace AiNative.Headless
{
    internal static class ReplayWriter
    {
        // 只接受本次已完成的实际检查点；调用者在执行成功且队列为空后发布。
        internal static byte[] Encode(byte[] initialSnapshot, byte[] commands, CliCheckpoint initial, IReadOnlyList<CliCheckpoint> checkpoints) =>
            CanonicalState.WriteBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("replayFormatVersion", 1);
                writer.WritePropertyName("initialSnapshot");
                writer.WriteRawValue(initialSnapshot);
                writer.WritePropertyName("commandFile");
                writer.WriteRawValue(commands);
                writer.WritePropertyName("initialCheckpoint");
                initial.Write(writer);
                writer.WriteStartArray("checkpoints");
                foreach (CliCheckpoint checkpoint in checkpoints)
                {
                    checkpoint.Write(writer);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
    }
}
