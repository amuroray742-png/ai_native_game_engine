using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AiNative.Hosting;

namespace AiNative.Headless
{
    internal static class JsonInput
    {
        internal static byte[] ReadFile(string path)
        {
            string? resolved = null;
            try
            {
                resolved = Path.GetFullPath(path);
                return File.ReadAllBytes(resolved);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                throw new HostOperationException("InputError", "InputReadFailed", "无法读取输入文件。", resolved, error);
            }
        }

        internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes) => JsonDocumentReader.Parse(bytes);
    }
}
