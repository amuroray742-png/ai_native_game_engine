using System;
using System.IO;
using System.Text;
using System.Text.Json;

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

        internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
        {
            JsonDocument? document = null;
            try
            {
                new UTF8Encoding(false, true).GetString(bytes.Span);
                document = JsonDocument.Parse(bytes);
                ProtocolJson.ValidateDocument(document.RootElement, "$");
                return document;
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is InvalidOperationException)
            {
                document?.Dispose();
                string path = error is JsonException json ? json.Path ?? "$" : "$";
                throw new HostOperationException("InputError", "InvalidJson", "JSON、UTF-8 或 Unicode 转义无效。", path, error);
            }
            catch
            {
                document?.Dispose();
                throw;
            }
        }
    }
}
