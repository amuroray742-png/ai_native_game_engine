using System;

namespace AiNative.GridGame
{
    public enum Side
    {
        A, B
    }

    public readonly struct GridPosition : IEquatable<GridPosition>
    {
        public GridPosition(int x, int y)
        {
            X = x;
            Y = y;
        }
        public int X { get; }
        public int Y { get; }
        public long DistanceTo(GridPosition other) => Math.Abs((long)X - other.X) + Math.Abs((long)Y - other.Y);
        public bool Equals(GridPosition other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is GridPosition other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
    }
}
