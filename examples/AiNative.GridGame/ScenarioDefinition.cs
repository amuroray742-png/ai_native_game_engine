using System.Collections.Generic;

namespace AiNative.GridGame
{
    /// <summary>宿主传入的场景数据；Create 校验并复制，不保留外部集合引用。</summary>
    public sealed class ScenarioDefinition
    {
        public ScenarioDefinition(int width, int height, IReadOnlyList<GridPosition> obstacles,
            IReadOnlyList<UnitDefinition> units, int actionPointsPerTurn, int attackDamage)
        {
            Width = width;
            Height = height;
            Obstacles = obstacles;
            Units = units;
            ActionPointsPerTurn = actionPointsPerTurn;
            AttackDamage = attackDamage;
        }

        public int Width { get; }
        public int Height { get; }
        public IReadOnlyList<GridPosition> Obstacles { get; }
        public IReadOnlyList<UnitDefinition> Units { get; }
        public int ActionPointsPerTurn { get; }
        public int AttackDamage { get; }
    }

    public sealed class UnitDefinition
    {
        public UnitDefinition(int id, Side side, GridPosition position, int health)
        {
            Id = id;
            Side = side;
            Position = position;
            Health = health;
        }

        public int Id { get; }
        public Side Side { get; }
        public GridPosition Position { get; }
        public int Health { get; }
    }
}
