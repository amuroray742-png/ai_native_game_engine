using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AiNative.GridGame;

namespace AiNative.Headless
{
    public static class Program
    {
        public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

        /// <summary>0=演示通过，1=执行或验收失败，2=输入配置错误。输出面向人，不是通用 CLI 协议。</summary>
        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            if (args.Length > 1)
            {
                error.WriteLine("用法: AiNative.Headless [scenario.json]");
                return 2;
            }
            string path = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "demo.json") : args[0];
            GridGameRules rules;
            try
            {
                rules = GridGameRules.Create(ScenarioLoader.Load(path));
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException
                || exception is IOException || exception is UnauthorizedAccessException)
            {
                error.WriteLine($"Configuration error ({path}): {exception.Message}");
                return 2;
            }

            try
            {
                IReadOnlyList<DemoStep> trace = DemoRunner.Run(rules);
                foreach (DemoStep step in trace)
                {
                    output.WriteLine($"Step {step.Step.Sequence}: {step.Command} => {step.Step.Result.Error ?? "Accepted"}");
                    foreach (var unit in step.State.Units)
                    {
                        output.WriteLine($"  {unit.Side} id={unit.Id} position={unit.Position} health={unit.Health}");
                    }
                    output.WriteLine($"  Turn={step.State.TurnIndex} Active={step.State.ActiveSide} AP={step.State.ActionPoints} Winner={step.State.Winner?.ToString() ?? "None"}");
                }
                if (!DemoRunner.HasExpectedOutcome(trace))
                {
                    error.WriteLine("Demo failed: 固定脚本执行失败或最终状态不符合预期。");
                    return 1;
                }
                output.WriteLine("Demo passed: Winner=A, Turn=3, A.Health=2, B.Health=0, AP=1");
                return 0;
            }
            catch (Exception exception)
            {
                // 进程边界保留完整诊断；发生故障后不会继续推进模拟。
                error.WriteLine($"Simulation failure: {exception}");
                return 1;
            }
        }
    }
}
