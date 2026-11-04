using System;
using AiNative.GridGame.Hosting;
using AiNative.Headless;
using AiNative.Hosting;
using AiNative.Physics.Jolt;
using AiNative.PhysicsSandbox.Hosting;

namespace AiNative.Examples.Runner
{
    public static class ExampleGames
    {
        // Explicit composition only. Constructing the registry does not initialize Jolt.
        public static GameRegistry CreateRegistry()
        {
            NativeBridgeLoader.Configure();
            return new GameRegistry(new GridGameModule(), new PhysicsSandboxModule(new JoltWorldFactory(), "ainative_jolt"));
        }
    }

    internal static class Program
    {
        private static int Main(string[] args) => new CliRunner(ExampleGames.CreateRegistry()).Run(args, Console.Out, Console.Error);
    }
}
