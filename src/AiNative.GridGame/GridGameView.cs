using System;
using System.Collections.Generic;

namespace AiNative.GridGame
{
    /// <summary>一个已完成逻辑步的隔离视图；集合与元素均不允许修改。</summary>
    public sealed class GridGameView
    {
        internal GridGameView(int width, int height, GridPosition[] obstacles, UnitDefinition[] units,
            Side activeSide, int actionPoints, long turnIndex, Side? winner)
        {
            Width = width;
            Height = height;
            Obstacles = Array.AsReadOnly((GridPosition[])obstacles.Clone());
            Units = Array.AsReadOnly((UnitDefinition[])units.Clone());
            ActiveSide = activeSide;
            ActionPoints = actionPoints;
            TurnIndex = turnIndex;
            Winner = winner;
        }

        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<GridPosition> Obstacles { get; }
        public IReadOnlyList<UnitDefinition> Units { get; }
        public Side ActiveSide { get; }
        public int ActionPoints { get; }
        public long TurnIndex { get; }
        public Side? Winner { get; }
    }
}
