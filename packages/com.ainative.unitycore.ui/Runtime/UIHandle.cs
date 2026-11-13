using System;

namespace AiNative.UnityCore.UI
{
    public readonly struct UIHandle : IEquatable<UIHandle>
    {
        internal readonly object Owner;
        internal readonly long Generation;
        internal readonly long Instance;
        internal readonly long Session;
        internal readonly bool CleanupOnly;

        internal UIHandle(object owner, long generation, long instance, long session, bool cleanupOnly = false)
        {
            Owner = owner;
            Generation = generation;
            Instance = instance;
            Session = session;
            CleanupOnly = cleanupOnly;
        }

        public bool Equals(UIHandle other) => ReferenceEquals(Owner, other.Owner) &&
            Generation == other.Generation && Instance == other.Instance && Session == other.Session &&
            CleanupOnly == other.CleanupOnly;
        public override bool Equals(object obj) => obj is UIHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Owner, Generation, Instance, Session, CleanupOnly);
        public static bool operator ==(UIHandle left, UIHandle right) => left.Equals(right);
        public static bool operator !=(UIHandle left, UIHandle right) => !left.Equals(right);
    }
}
