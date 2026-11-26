using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace AiNative.Server.Protocol.Serialization
{
    internal static class JsonFields
    {
        internal static bool Unique(JsonElement value)
        {
            try { return UniqueAndDecodable(value); }
            catch (InvalidOperationException) { return false; }
        }

        // JSON 字节合法不代表转义后的 UTF-16 合法；也校验不透明游戏正文。
        private static bool UniqueAndDecodable(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name) || !UniqueAndDecodable(property.Value)) { return false; }
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!UniqueAndDecodable(item)) { return false; }
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                _ = value.GetString();
            }
            return true;
        }

        internal static bool Exact(JsonElement value, params string[] names)
        {
            if (value.ValueKind != JsonValueKind.Object) { return false; }
            int count = 0;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (Array.IndexOf(names, property.Name) < 0) { return false; }
                count++;
            }
            return count == names.Length;
        }

        internal static bool Text(JsonElement value, int maximum = 128) =>
            value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 0 && value.GetString()!.Length <= maximum;

        internal static bool Uuid(JsonElement value) =>
            value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 36 && Guid.TryParseExact(value.GetString(), "D", out _);

        internal static bool Counter(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String) { return false; }
            string text = value.GetString()!;
            if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) { return false; }
            foreach (char character in text)
            {
                if (character < '0' || character > '9') { return false; }
            }
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        internal static bool Boolean(JsonElement value) => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
        internal static bool Nullable(JsonElement value, Func<JsonElement, bool> validate) => value.ValueKind == JsonValueKind.Null || validate(value);
        internal static bool Object(JsonElement value) => value.ValueKind == JsonValueKind.Object;
        internal static bool Seat(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int seat) && (seat == 0 || seat == 1);
        internal static bool Match(JsonElement value) => Exact(value, "controlEpoch", "roomId") && Uuid(value.GetProperty("controlEpoch")) && Uuid(value.GetProperty("roomId"));
        internal static bool OneOf(JsonElement value, params string[] options) => value.ValueKind == JsonValueKind.String && Array.IndexOf(options, value.GetString()) >= 0;
    }
}
