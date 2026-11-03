using System;
using System.IO;
using System.Runtime.InteropServices;
using AiNative.Physics.Jolt;

namespace AiNative.Examples.Runner
{
    internal static class NativeBridgeLoader
    {
        // DllImport pointers remain bound for the process lifetime. World resources are owned separately.
        private static readonly Lazy<IntPtr> Library = new Lazy<IntPtr>(() => NativeLibrary.Load(Path.Combine(
            AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ainative_jolt.dll" : "libainative_jolt.so")));
        private static readonly Lazy<bool> Configured = new Lazy<bool>(() =>
        {
            NativeLibrary.SetDllImportResolver(typeof(JoltWorldFactory).Assembly,
                (name, _, _) => name == "ainative_jolt" ? Library.Value : IntPtr.Zero);
            return true;
        });

        internal static void Configure()
        {
            // Installing the resolver performs no native load; GridGame and compare stay native-free.
            _ = Configured.Value;
        }
    }
}
