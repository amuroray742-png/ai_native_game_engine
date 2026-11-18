using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;
using AiNative.Physics.Jolt;
using AiNative.Unity;
using UnityEngine;

namespace AiNative.Examples.PhysicsPlay
{
    internal static class SandboxIdentity
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileNameW(IntPtr module, StringBuilder path, int capacity);

        internal static HostEnvironment Read(IGameModule module)
        {
            // Resolve the loaded image, including Unity's shadow-copy location.
            _ = JoltWorldFactory.ActiveWorldCount;
            var path = new StringBuilder(32768);
            IntPtr library = GetModuleHandleW("ainative_jolt.dll");
            if (library == IntPtr.Zero || GetModuleFileNameW(library, path, path.Capacity) == 0)
            {
                throw new InvalidOperationException("Cannot identify loaded ainative_jolt.dll.");
            }
            byte[] manifest = File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "native-build.json"));
            string binaryHash = JsonData.Hash(File.ReadAllBytes(path.ToString()));
            if (JsonData.Parse(manifest).GetProperty("binaryHash").GetString() != binaryHash)
            {
                throw new InvalidOperationException("Loaded Jolt library differs from the build manifest.");
            }
#if !ENABLE_IL2CPP
            var assemblies = new List<Assembly> { typeof(JsonDocument).Assembly, typeof(RuntimeState).Assembly,
                typeof(IGameModule).Assembly, typeof(SandboxScene).Assembly, typeof(PlayHost<,>).Assembly,
                typeof(Application).Assembly, typeof(UnityEngine.UI.Button).Assembly, typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly,
                typeof(System.IO.Pipelines.PipeReader).Assembly };
            assemblies.AddRange(module.CodeTypes.Select(t => t.Assembly));
            foreach (var dependency in typeof(JsonDocument).Assembly.GetReferencedAssemblies())
            {
                if (dependency.Name == "Microsoft.Bcl.AsyncInterfaces" || dependency.Name == "System.Runtime.CompilerServices.Unsafe")
                {
                    assemblies.Add(Assembly.Load(dependency));
                }
            }
#endif
            return new HostEnvironment("Unity " + Application.unityVersion + " / " +
#if ENABLE_IL2CPP
                "IL2CPP",
#else
                "Mono " + Environment.Version,
#endif
                Application.platform.ToString(), RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                JsonData.Write(writer =>
                {
                    writer.WriteStartObject();
#if ENABLE_IL2CPP
                    // AOT assemblies have no file location. The generated code and metadata are
                    // the executable identity used for strict Player restore in this backend.
                    string playerDirectory = Path.GetDirectoryName(Application.dataPath);
                    writer.WriteString("GameAssembly.dll", JsonData.Hash(File.ReadAllBytes(Path.Combine(playerDirectory, "GameAssembly.dll"))));
                    writer.WriteString("global-metadata.dat", JsonData.Hash(File.ReadAllBytes(
                        Path.Combine(Application.dataPath, "il2cpp_data", "Metadata", "global-metadata.dat"))));
#else
                    foreach (var assembly in assemblies.Distinct())
                    {
                        writer.WriteString(assembly.GetName().Name, JsonData.Hash(File.ReadAllBytes(assembly.Location)));
                    }
#endif
                    writer.WriteString("ainative_jolt.dll", binaryHash);
                    writer.WriteString("ainative_jolt.build", JsonData.Hash(manifest));
                    writer.WriteEndObject();
                }));
        }
    }
}
