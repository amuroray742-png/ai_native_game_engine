using System;
using System.Collections.Generic;

namespace AiNative.GridGame
{
    internal static class ScenarioValidator
    {
        internal static void Validate(ScenarioDefinition definition)
        {
            if (definition.Width <= 0 || definition.Height <= 0)
            {
                throw new ArgumentException("width 和 height 必须为正整数。");
            }
            if (definition.ActionPointsPerTurn <= 0 || definition.AttackDamage <= 0)
            {
                throw new ArgumentException("actionPointsPerTurn 和 attackDamage 必须为正整数。");
            }
            if (definition.Obstacles == null || definition.Units == null || definition.Units.Count != 2)
            {
                throw new ArgumentException("obstacles 不可为空，units 必须包含双方各一个单位。");
            }
            var occupied = new HashSet<GridPosition>();
            foreach (GridPosition obstacle in definition.Obstacles)
            {
                if (!Contains(definition, obstacle) || !occupied.Add(obstacle))
                {
                    throw new ArgumentException($"obstacles 中存在越界或重复位置 {obstacle}。");
                }
            }
            var ids = new HashSet<int>();
            var sides = new HashSet<Side>();
            foreach (UnitDefinition unit in definition.Units)
            {
                if (unit == null)
                {
                    throw new ArgumentException("units 不允许包含 null。");
                }
                if (unit.Id <= 0 || !ids.Add(unit.Id))
                {
                    throw new ArgumentException($"units 中的实体 ID {unit.Id} 必须为正数且唯一。");
                }
                if ((unit.Side != Side.A && unit.Side != Side.B) || !sides.Add(unit.Side))
                {
                    throw new ArgumentException($"单位 {unit.Id} 的 side 无效或重复。");
                }
                if (unit.Health <= 0 || !Contains(definition, unit.Position) || !occupied.Add(unit.Position))
                {
                    throw new ArgumentException($"单位 {unit.Id} 的 health 必须为正数，position 必须在地图内且无占用。");
                }
            }
        }

        private static bool Contains(ScenarioDefinition definition, GridPosition position) =>
            position.X >= 0 && position.Y >= 0 && position.X < definition.Width && position.Y < definition.Height;
    }
}
