namespace AiNative.GridGame
{
    public abstract class GridCommand
    {
        private protected GridCommand(Side side)
        {
            Side = side;
        }
        public Side Side { get; }
    }

    public sealed class Move : GridCommand
    {
        public Move(Side side, int unitId, MoveDirection direction) : base(side)
        {
            UnitId = unitId;
            Direction = direction;
        }
        public int UnitId { get; }
        public MoveDirection Direction { get; }
    }

    public sealed class Attack : GridCommand
    {
        public Attack(Side side, int unitId, int targetId) : base(side)
        {
            UnitId = unitId;
            TargetId = targetId;
        }
        public int UnitId { get; }
        public int TargetId { get; }
    }

    public sealed class EndTurn : GridCommand
    {
        public EndTurn(Side side) : base(side) { }
    }
}
