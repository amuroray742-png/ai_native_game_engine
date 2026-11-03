using System;
using System.Text.Json;

namespace AiNative.Hosting
{
    /// <summary>由宿主显式提供；本类型不读取环境或文件。</summary>
    public sealed class HostEnvironment
    {
        public HostEnvironment(string runtime, string os, string architecture, JsonElement components)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            Os = os ?? throw new ArgumentNullException(nameof(os));
            Architecture = architecture ?? throw new ArgumentNullException(nameof(architecture));
            foreach (JsonProperty item in components.EnumerateObject())
            {
                ProtocolJson.Hash(item.Value, "$.environment.components." + item.Name);
            }
            Components = components.Clone();
            BuildHash = JsonData.Hash(Components);
        }
        public string Runtime { get; }
        public string Os { get; }
        public string Architecture { get; }
        public JsonElement Components { get; }
        public string BuildHash { get; }
        public JsonElement ToJson() => JsonData.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("runtime", Runtime);
            writer.WriteString("os", Os);
            writer.WriteString("architecture", Architecture);
            writer.WriteString("buildHash", BuildHash);
            writer.WritePropertyName("components");
            Components.WriteTo(writer);
            writer.WriteEndObject();
        });

        public static HostEnvironment Read(JsonElement value)
        {
            ProtocolJson.Object(value, "$.environment", "runtime", "os", "architecture", "buildHash", "components");
            JsonElement components = value.GetProperty("components");
            JsonData.Require(components.ValueKind == JsonValueKind.Object, "InvalidProtocol", "$.environment.components");
            var environment = new HostEnvironment(ProtocolJson.String(value.GetProperty("runtime"), "$.environment.runtime"),
                ProtocolJson.String(value.GetProperty("os"), "$.environment.os"),
                ProtocolJson.String(value.GetProperty("architecture"), "$.environment.architecture"), components);
            JsonData.Require(environment.BuildHash == ProtocolJson.Hash(value.GetProperty("buildHash"), "$.environment.buildHash"),
                "BuildHashMismatch", "$.environment.buildHash");
            return environment;
        }

        public void RequireMatch(HostEnvironment expected)
        {
            JsonData.Require(Runtime == expected.Runtime && Os == expected.Os && Architecture == expected.Architecture
                && BuildHash == expected.BuildHash, "EnvironmentMismatch", "$.environment");
        }
    }
}
