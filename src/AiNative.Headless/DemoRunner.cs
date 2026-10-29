using System;
using System.Collections.Generic;
using System.Linq;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Headless
{
    public static class DemoRunner
    {
        public const ulong Seed = 42;

        /// <summary>固定六步演示，配置可替换但必须满足该脚本的起点和验收目标。</summary>
        public static IReadOnlyList<DemoStep> Run(ScenarioDefinition definition)
        {
            return Run(GridGameRules.Create(definition));
        }

        internal static IReadOnlyList<DemoStep> Run(GridGameRules rules)
        {
            var session = new SimulationSession<GridCommand, GridGameView>(rules, Seed);
            GridGameView initial = session.GetState();
            int a = initial.Units.Single(unit => unit.Side == Side.A).Id;
            int b = initial.Units.Single(unit => unit.Side == Side.B).Id;
            GridCommand[] commands =
            {
                new Move(Side.A, a, new GridPosition(1, 0)),
                new Attack(Side.A, a, b),
                new EndTurn(Side.A),
                new Attack(Side.B, b, a),
                new EndTurn(Side.B),
                new Attack(Side.A, a, b)
            };
            var trace = new List<DemoStep>();
            foreach (GridCommand command in commands)
            {
                session.Submit(command);
                StepResult step = session.Advance() ?? throw new InvalidOperationException("演示命令未被执行。");
                GridGameView state = session.GetState();
                trace.Add(new DemoStep(command.GetType().Name, step, state));
                if (!step.Result.Succeeded)
                {
                    return trace.AsReadOnly();
                }
            }
            return trace.AsReadOnly();
        }

        public static bool HasExpectedOutcome(IReadOnlyList<DemoStep> trace)
        {
            if (trace.Count != 6 || trace.Any(step => !step.Step.Result.Succeeded))
            {
                return false;
            }
            GridGameView state = trace[trace.Count - 1].State;
            return state.Winner == Side.A && state.ActiveSide == Side.A && state.TurnIndex == 3
                && state.ActionPoints == 1
                && state.Units.Single(unit => unit.Side == Side.A).Health == 2
                && state.Units.Single(unit => unit.Side == Side.B).Health == 0;
        }
    }

    public sealed class DemoStep
    {
        public DemoStep(string command, StepResult step, GridGameView state)
        {
            Command = command;
            Step = step;
            State = state;
        }

        public string Command { get; }
        public StepResult Step { get; }
        public GridGameView State { get; }
    }
}
