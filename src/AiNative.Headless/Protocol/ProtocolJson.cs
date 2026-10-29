using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AiNative.GridGame;

namespace AiNative.Headless
{
    /// <summary>宿主协议 JSON 的严格原语读取，所有错误保留字段路径。</summary>
    internal static class ProtocolJson
    {
        internal static void ValidateDocument(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string child = path + "." + property.Name;
                    if (!names.Add(property.Name))
                    {
                        throw new HostOperationException("InputError", "InvalidJson", "属性重复。", child);
                    }
                    ValidateDocument(property.Value, child);
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                // 强制反转义，拒绝 JsonDocument 延迟保留的未配对 UTF-16 surrogate。
                element.GetString();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateDocument(item, $"{path}[{index++}]");
                }
            }
        }

        internal static void Object(JsonElement element, string path, params string[] fields)
        {
            Require(element.ValueKind == JsonValueKind.Object, path, "需要对象。");
            foreach (string field in fields)
            {
                Require(element.TryGetProperty(field, out _), path + "." + field, "缺少必填字段。");
            }
            var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(allowed.Contains(property.Name), path + "." + property.Name, "不允许未知字段。");
            }
        }

        internal static JsonElement.ArrayEnumerator Array(JsonElement element, string path)
        {
            Require(element.ValueKind == JsonValueKind.Array, path, "需要数组。");
            return element.EnumerateArray();
        }

        internal static string String(JsonElement element, string path)
        {
            Require(element.ValueKind == JsonValueKind.String, path, "需要非 null 字符串。");
            return element.GetString() ?? throw new InvalidOperationException("字符串节点不可为 null。");
        }

        internal static int Int32(JsonElement element, string path)
        {
            Require(element.ValueKind == JsonValueKind.Number, path, "需要 Int32 整数。");
            string token = element.GetRawText();
            Require(!token.Contains('.') && !token.Contains('e') && !token.Contains('E'), path, "不允许小数或指数。");
            Require(element.TryGetInt32(out int value), path, "整数超出 Int32 范围。");
            return value;
        }

        internal static ulong UInt64(JsonElement element, string path)
        {
            string token = String(element, path);
            Require(token.Length > 0 && (token.Length == 1 || token[0] != '0'), path, "需要无前导零的十进制字符串。");
            foreach (char character in token)
            {
                Require(character >= '0' && character <= '9', path, "只允许 ASCII 十进制数字。");
            }
            Require(ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value), path, "超出 UInt64 范围。");
            return value;
        }

        internal static long Int64(JsonElement element, string path)
        {
            ulong value = UInt64(element, path);
            Require(value <= long.MaxValue, path, "超出非负 Int64 范围。");
            return (long)value;
        }

        internal static string Hash(JsonElement element, string path)
        {
            string value = String(element, path);
            Require(value.Length == 64, path, "摘要必须为 64 位小写十六进制。");
            foreach (char character in value)
            {
                Require((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'), path, "摘要必须为小写十六进制。");
            }
            return value;
        }

        internal static Side SideValue(JsonElement element, string path)
        {
            string value = String(element, path);
            Require(value == "A" || value == "B", path, "阵营必须为 A 或 B。");
            return value == "A" ? Side.A : Side.B;
        }

        private static void Require(bool condition, string path, string message)
        {
            if (!condition)
            {
                throw new HostOperationException("InputError", "InvalidProtocol", message, path);
            }
        }
    }
}
