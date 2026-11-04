using AiNative.Core;

namespace AiNative.GridGame
{
    public sealed class UnitMoved : GameEvent
    {
        public UnitMoved(int unitId, GridPosition from, GridPosition to)
        {
            UnitId = unitId;
            From = from;
            To = to;
        }
        public int UnitId { get; }
        public GridPosition From { get; }
        public GridPosition To { get; }
    }

    public sealed class UnitDamaged : GameEvent
    {
        public UnitDamaged(int unitId, int damage, int health)
        {
            UnitId = unitId;
            Damage = damage;
            Health = health;
        }
        public int UnitId { get; }
        public int Damage { get; }
        public int Health { get; }
    }

    public sealed class TurnChanged : GameEvent
    {
        public TurnChanged(Side side, long turnIndex)
        {
            Side = side;
            TurnIndex = turnIndex;
        }
        public Side Side { get; }
        public long TurnIndex { get; }
    }

    public sealed class GameEnded : GameEvent
    {
        public GameEnded(Side winner)
        {
            Winner = winner;
        }
        public Side Winner { get; }
    }
}
