using System;
using System.Text;
using System.Text.Json;

namespace AiNative.Hosting
{
    public static class JsonDocumentReader
    {
        public static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
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
                throw new ProtocolException("InvalidJson", "JSON、UTF-8 或 Unicode 转义无效。", path, error);
            }
            catch
            {
                document?.Dispose();
                throw;
            }
        }
    }
}
