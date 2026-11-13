using System.Collections.Generic;

namespace AiNative.UnityCore.UI
{
    // Entries remain charged to capacity until cleanup really succeeds.
    internal sealed class UIWindowCache
    {
        private readonly LinkedList<UIWindowRecord> _entries = new LinkedList<UIWindowRecord>();
        private long _order;
        internal readonly int Capacity;
        internal int Count => _entries.Count;
        internal UIWindowRecord Oldest => _entries.First.Value;
        internal UIWindowCache(int capacity) { Capacity = capacity; }
        internal bool Contains(UIWindowRecord record) => _entries.Contains(record);
        internal void Remove(UIWindowRecord record) { _entries.Remove(record); }
        internal UIWindowRecord[] Snapshot()
        {
            var result = new UIWindowRecord[_entries.Count];
            _entries.CopyTo(result, 0);
            return result;
        }
        internal void Add(UIWindowRecord record, bool restore)
        {
            if (!restore) { record.CacheOrder = ++_order; }
            var next = _entries.First;
            while (next != null && next.Value.CacheOrder < record.CacheOrder) { next = next.Next; }
            if (next == null) { _entries.AddLast(record); }
            else { _entries.AddBefore(next, record); }
            record.Cached = true;
        }
    }
}
