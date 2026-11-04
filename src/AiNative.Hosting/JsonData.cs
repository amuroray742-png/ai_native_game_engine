using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiNative.Hosting
{
    public static class JsonData
    {
        public static JsonElement Parse(ReadOnlyMemory<byte> bytes)
        {
            using JsonDocument document = JsonDocumentReader.Parse(bytes);
            return document.RootElement.Clone();
        }

        public static JsonElement Write(Action<Utf8JsonWriter> write) => Parse(Bytes(write));

        public static byte[] Bytes(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                write(writer);
            }
            return stream.ToArray();
        }

        public static byte[] CanonicalBytes(JsonElement value) => Bytes(writer => WriteCanonical(writer, value));
        public static string Hash(JsonElement value) => Hash(CanonicalBytes(value));

        public static string Hash(byte[] bytes)
        {
            using SHA256 hash = SHA256.Create();
            var result = new StringBuilder(64);
            foreach (byte value in hash.ComputeHash(bytes))
            {
                result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
            }
            else
            {
                value.WriteTo(writer);
            }
        }

        public static JsonElement EmptyObject => Write(writer => { writer.WriteStartObject(); writer.WriteEndObject(); });
        internal static JsonElement WithProperty(JsonElement source, string name, JsonElement replacement) => Write(writer =>
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in source.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == name)
                {
                    replacement.WriteTo(writer);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        });
        public static void Require(bool condition, string code, string path)
        {
            if (!condition)
            {
                throw new ProtocolException(code, "校验失败：" + code, path);
            }
        }

        public static void Version(JsonElement root, string field = "formatVersion")
        {
            Require(root.ValueKind == JsonValueKind.Object, "InvalidProtocol", "$");
            if (!root.TryGetProperty(field, out JsonElement version))
            {
                // 识别旧记录，只报告不支持，绝不隐式转成 v2。
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (property.Name.EndsWith("FormatVersion", StringComparison.Ordinal))
                    {
                        throw new ProtocolException("UnsupportedVersion", "请重新生成 v2 记录。", "$" + "." + property.Name);
                    }
                }
                throw new ProtocolException("InvalidProtocol", "缺少版本。", "$." + field);
            }
            Require(ProtocolJson.Int32(version, "$." + field) == 2, "UnsupportedVersion", "$." + field);
        }
    }
}
