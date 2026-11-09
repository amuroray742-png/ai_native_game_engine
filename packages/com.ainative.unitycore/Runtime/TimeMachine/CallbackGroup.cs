#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine.Profiling;

namespace AiNative.UnityCore.TimeMachine
{
    // The four Unity callback phases share registration and dispatch semantics.
    internal sealed class CallbackGroup<T>
    {
        private sealed class Entry
        {
            internal readonly int Id;
            internal readonly Action<T> Action;
            internal readonly string Sample;
            internal readonly int Order;

            internal Entry(int id, Action<T> action, string sample, int order)
            {
                Id = id;
                Action = action;
                Sample = sample;
                Order = order;
            }
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly Dictionary<Action<T>, int> _ids = new Dictionary<Action<T>, int>();
        private bool _dispatching;

        internal void Register(int id, Action<T> action, string sample, int order)
        {
            Remove(action);
            _entries.Add(id, new Entry(id, action, sample, order));
            _ids.Add(action, id);
        }

        internal bool Contains(int id) { return _entries.ContainsKey(id); }

        internal void Remove(Action<T> action)
        {
            if (action == null) { throw new ArgumentNullException(nameof(action)); }
            if (_ids.TryGetValue(action, out int id)) { Remove(id); }
        }

        internal void Remove(int id)
        {
            if (_entries.TryGetValue(id, out Entry entry))
            {
                _entries.Remove(id);
                _ids.Remove(entry.Action);
            }
        }

        internal void Clear()
        {
            _entries.Clear();
            _ids.Clear();
        }

        internal void Dispatch(T argument)
        {
            if (_dispatching) { throw new InvalidOperationException("Cannot reenter a callback phase."); }
            if (_entries.Count == 0) { return; }
            var snapshot = new List<Entry>(_entries.Values);
            snapshot.Sort((left, right) =>
            {
                int comparison = right.Order.CompareTo(left.Order);
                return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
            });
            _dispatching = true;
            try
            {
                foreach (Entry entry in snapshot)
                {
                    if (!Contains(entry.Id)) { continue; }
                    Profiler.BeginSample(entry.Sample);
                    try { entry.Action(argument); }
                    finally { Profiler.EndSample(); }
                }
            }
            finally { _dispatching = false; }
        }
    }
}
