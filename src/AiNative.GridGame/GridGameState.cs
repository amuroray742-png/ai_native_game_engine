using System;
using System.Collections.Generic;
using System.Linq;

namespace AiNative.GridGame
{
    /// <summary>包含原始配置和当前玩法数据的隔离状态；构造成功不代表通过恢复校验。</summary>
    public sealed class GridGameState
    {
        /// <summary>复制配置及当前单位集合并拒绝 null；领域不变量由 GridGameRules.Restore 校验。</summary>
        public GridGameState(ScenarioDefinition configuration, IReadOnlyList<UnitDefinition> units,
            Side activeSide, int actionPoints, long turnIndex, Side? winner)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }
            if (configuration.Obstacles == null)
            {
                throw new ArgumentNullException(nameof(configuration.Obstacles));
            }
            Configuration = new ScenarioDefinition(configuration.Width, configuration.Height,
                Array.AsReadOnly(configuration.Obstacles.OrderBy(position => position.Y).ThenBy(position => position.X).ToArray()),
                CopyUnits(configuration.Units), configuration.ActionPointsPerTurn, configuration.AttackDamage);
            Units = CopyUnits(units);
            ActiveSide = activeSide;
            ActionPoints = actionPoints;
            TurnIndex = turnIndex;
            Winner = winner;
        }

        public ScenarioDefinition Configuration { get; }
        public IReadOnlyList<UnitDefinition> Units { get; }
        public Side ActiveSide { get; }
        public int ActionPoints { get; }
        public long TurnIndex { get; }
        public Side? Winner { get; }

        private static IReadOnlyList<UnitDefinition> CopyUnits(IReadOnlyList<UnitDefinition> units)
        {
            if (units == null)
            {
                throw new ArgumentNullException(nameof(units));
            }
            if (units.Any(unit => unit == null))
            {
                throw new ArgumentException("单位集合不允许包含 null。", nameof(units));
            }
            // UnitDefinition 自身不可变，只需隔离集合，后续规则通过替换元素更新状态。
            return Array.AsReadOnly(units.OrderBy(unit => unit.Id).ToArray());
        }
    }
}
