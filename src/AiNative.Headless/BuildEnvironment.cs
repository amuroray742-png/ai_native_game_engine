using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using AiNative.Core;
using AiNative.Hosting;

namespace AiNative.Headless
{
    public static class BuildEnvironment
    {
        public static HostEnvironment Current(IGameModule module)
        {
            try
            {
                var assemblies = new List<Assembly>
                {
                    typeof(RuntimeState).Assembly, typeof(IGameModule).Assembly, typeof(BuildEnvironment).Assembly,
                    typeof(System.Text.Json.JsonDocument).Assembly, typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly,
                    typeof(System.IO.Pipelines.PipeReader).Assembly
                };
                Assembly? entry = Assembly.GetEntryAssembly();
                if (entry != null)
                {
                    assemblies.Add(entry);
                }
                assemblies.AddRange(module.CodeTypes.Select(type => type.Assembly));
                var components = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (Assembly assembly in assemblies.Distinct())
                {
                    components.Add(assembly.GetName().Name!, JsonData.Hash(File.ReadAllBytes(assembly.Location)));
                }
                string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
                foreach (string library in module.NativeLibraries)
                {
                    if (os == "unsupported" || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    {
                        throw new PlatformNotSupportedException("物理宿主只验收 Windows/Linux x64。");
                    }
                    string name = os == "windows" ? library + ".dll" : "lib" + library + ".so";
                    components.Add(name, JsonData.Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))));
                    components.Add(library + ".build", JsonData.Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-build.json"))));
                }
                return new HostEnvironment(Environment.Version.ToString(), os, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    JsonData.Write(writer =>
                    {
                        writer.WriteStartObject();
                        foreach (KeyValuePair<string, string> item in components)
                        {
                            writer.WriteString(item.Key, item.Value);
                        }
                        writer.WriteEndObject();
                    }));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException)
            {
                throw new HostOperationException("HostFailure", "BuildIdentityUnavailable", "无法读取实际构建身份。", inner: error);
            }
        }
    }
}
