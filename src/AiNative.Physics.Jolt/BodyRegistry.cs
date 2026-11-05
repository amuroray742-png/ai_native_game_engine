using System;
using System.Collections.Generic;

namespace AiNative.Physics.Jolt
{
    internal sealed class BodyEntry
    {
        internal BodyEntry(BodyDefinition definition, uint bodyId)
        {
            Definition = definition;
            BodyId = bodyId;
        }
        internal BodyDefinition Definition { get; }
        internal uint BodyId { get; }
    }

    /// <summary>实体身份及 BodyID 分配的唯一所有者；删除身份保留至事件消费完成。</summary>
    internal sealed class BodyRegistry
    {
        internal const int Capacity = 1024;
        private readonly SortedDictionary<ulong, BodyEntry> _entries = new SortedDictionary<ulong, BodyEntry>();
        private readonly Dictionary<uint, ulong> _identities = new Dictionary<uint, ulong>();
        private readonly bool[] _occupied = new bool[Capacity];
        private readonly byte[] _generations;

        internal BodyRegistry(byte[]? generations = null)
        {
            _generations = generations == null ? new byte[Capacity] : (byte[])generations.Clone();
        }
        internal int Count => _entries.Count;
        internal IEnumerable<BodyEntry> Entries => _entries.Values;
        internal bool HasRetiredBodies => _identities.Count != _entries.Count;
        internal byte[] CopyGenerations() => (byte[])_generations.Clone();

        internal BodyEntry Find(ulong entity)
        {
            if (!_entries.TryGetValue(entity, out BodyEntry? entry))
            {
                throw new InvalidOperationException("Unknown physics entity.");
            }
            return entry;
        }

        internal ulong EntityFor(uint bodyId)
        {
            if (!_identities.TryGetValue(bodyId, out ulong entity))
            {
                throw new InvalidOperationException("Contact or observation has an unknown BodyID.");
            }
            return entity;
        }

        internal BodyEntry Reserve(BodyDefinition definition, uint? restoredBodyId = null)
        {
            if (_entries.ContainsKey(definition.Id))
            {
                throw new InvalidOperationException("Duplicate physics entity.");
            }
            uint bodyId = restoredBodyId ?? AllocateId();
            int slot = Slot(bodyId);
            if ((bodyId & 0x80000000) != 0 || slot >= Capacity || _occupied[slot]
                || _identities.ContainsKey(bodyId) || _generations[slot] != (byte)(bodyId >> 23))
            {
                throw new InvalidOperationException("Invalid body allocation.");
            }
            var entry = new BodyEntry(definition, bodyId);
            _entries.Add(definition.Id, entry);
            try
            {
                _identities.Add(bodyId, definition.Id);
            }
            catch
            {
                _entries.Remove(definition.Id);
                throw;
            }
            _occupied[slot] = true;
            return entry;
        }

        internal void CancelReservation(BodyEntry entry)
        {
            _entries.Remove(entry.Definition.Id);
            _identities.Remove(entry.BodyId);
            _occupied[Slot(entry.BodyId)] = false;
        }

        internal void Remove(BodyEntry entry)
        {
            _entries.Remove(entry.Definition.Id);
            int slot = Slot(entry.BodyId);
            _occupied[slot] = false;
            _generations[slot] = unchecked((byte)(_generations[slot] + 1));
            // Keep the old BodyID even after generation wraps until Step delivers its contacts.
        }

        internal void CompleteStep()
        {
            if (!HasRetiredBodies)
            {
                return;
            }
            _identities.Clear();
            foreach (BodyEntry entry in _entries.Values)
            {
                _identities.Add(entry.BodyId, entry.Definition.Id);
            }
        }

        private uint AllocateId()
        {
            for (int slot = 0; slot < Capacity; slot++)
            {
                uint id = (uint)slot | ((uint)_generations[slot] << 23);
                if (!_occupied[slot] && !_identities.ContainsKey(id))
                {
                    return id;
                }
            }
            throw new InvalidOperationException("Body capacity exceeded or IDs await contact consumption.");
        }

        internal static int Slot(uint bodyId) => (int)(bodyId & 0x7fffff);
    }
}
