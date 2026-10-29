using System;
using System.Collections.Generic;
using System.Linq;

namespace AiNative.GridGame
{
    internal static class GridGameStateValidator
    {
        internal static void Validate(GridGameState state)
        {
            ScenarioDefinition configuration = state.Configuration;
            ScenarioValidator.Validate(configuration);
            if (state.Units.Count != configuration.Units.Count)
            {
                throw new ArgumentException("运行单位必须与配置中的单位一一对应。", nameof(state));
            }
            var ids = new HashSet<int>();
            var occupied = new HashSet<GridPosition>(configuration.Obstacles);
            foreach (UnitDefinition unit in state.Units)
            {
                UnitDefinition? initial = configuration.Units.FirstOrDefault(candidate => candidate.Id == unit.Id);
                if (initial == null || !ids.Add(unit.Id) || unit.Side != initial.Side)
                {
                    throw new ArgumentException($"单位 {unit.Id} 的 ID 或阵营与配置不一致。", nameof(state));
                }
                if (unit.Health < 0 || unit.Health > initial.Health)
                {
                    throw new ArgumentException($"单位 {unit.Id} 的生命值超出初始配置范围。", nameof(state));
                }
                if (unit.Position.X < 0 || unit.Position.Y < 0
                    || unit.Position.X >= configuration.Width || unit.Position.Y >= configuration.Height
                    || !occupied.Add(unit.Position))
                {
                    throw new ArgumentException($"单位 {unit.Id} 的位置越界或被占用。", nameof(state));
                }
            }
            if (state.ActionPoints < 0 || state.ActionPoints > configuration.ActionPointsPerTurn)
            {
                throw new ArgumentException("行动点超出配置范围。", nameof(state));
            }
            Side expectedSide = state.TurnIndex % 2 == 1 ? Side.A : Side.B;
            if (state.TurnIndex < 1 || state.ActiveSide != expectedSide)
            {
                throw new ArgumentException("回合编号或当前行动方不合法。", nameof(state));
            }
            if (state.Winner == null)
            {
                if (state.Units.Any(unit => unit.Health == 0))
                {
                    throw new ArgumentException("未结束的对局必须双方存活。", nameof(state));
                }
            }
            else
            {
                if (state.Winner != state.ActiveSide
                    || state.Units.Any(unit => unit.Side == state.Winner ? unit.Health == 0 : unit.Health != 0))
                {
                    throw new ArgumentException("胜方、当前方与单位生命值不一致。", nameof(state));
                }
            }
        }
    }
}
