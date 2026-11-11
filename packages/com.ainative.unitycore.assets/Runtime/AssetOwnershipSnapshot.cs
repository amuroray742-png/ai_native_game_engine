using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetOwnershipRecord
    {
        public long Id { get; }
        public string Kind { get; }
        public string Name { get; }
        public long OwnerId { get; }
        public string ResourceKey { get; }
        public string Bundle { get; }
        public string State { get; }
        public IReadOnlyList<string> Dependencies { get; }
        internal AssetOwnershipRecord(long id, string kind, string name, long ownerId, string resourceKey, string bundle,
            string state, IEnumerable<string> dependencies = null)
        {
            Id = id; Kind = kind; Name = name; OwnerId = ownerId; ResourceKey = resourceKey; Bundle = bundle; State = state;
            Dependencies = Array.AsReadOnly((dependencies ?? Array.Empty<string>()).ToArray());
        }
    }

    public sealed class AssetOperationRecord
    {
        public long Id { get; }
        public string Name { get; }
        public string State { get; }
        public double QueueMilliseconds { get; }
        public double LoadMilliseconds { get; }
        internal AssetOperationRecord(long id, string name, string state, double queue, double load)
        { Id = id; Name = name; State = state; QueueMilliseconds = queue; LoadMilliseconds = load; }
    }

    public sealed class AssetOwnershipSnapshot
    {
        public string Generation { get; }
        public IReadOnlyList<AssetOwnershipRecord> Owners { get; }
        public IReadOnlyList<AssetOperationRecord> RecentOperations { get; }
        public IReadOnlyDictionary<string, int> Peaks { get; }
        public long SharedHits { get; }
        public long Failures { get; }
        public long Cancellations { get; }
        public int ActiveLoads { get; }
        public int QueuedLoads { get; }
        public int PeakLoads { get; }
        internal AssetOwnershipSnapshot(string generation, IEnumerable<AssetOwnershipRecord> owners,
            IEnumerable<AssetOperationRecord> operations, Dictionary<string, int> peaks,
            long hits, long failures, long cancellations, AssetLoadScheduler scheduler)
        {
            Generation = generation;
            Owners = Array.AsReadOnly(owners.OrderBy(x => x.Id).ToArray());
            RecentOperations = Array.AsReadOnly(operations.ToArray());
            Peaks = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(peaks));
            SharedHits = hits; Failures = failures; Cancellations = cancellations;
            ActiveLoads = scheduler.Active; QueuedLoads = scheduler.Queued; PeakLoads = scheduler.Peak;
        }
    }

    internal sealed class AssetOwnershipTracker
    {
        private readonly string _generation = Guid.NewGuid().ToString("N");
        private readonly Dictionary<long, AssetOwnershipRecord> _owners = new Dictionary<long, AssetOwnershipRecord>();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _peaks = new Dictionary<string, int>();
        private readonly Queue<AssetOperationRecord> _history = new Queue<AssetOperationRecord>();
        private long _next;
        internal long SharedHits;
        internal long Failures;
        internal long Cancellations;
        internal long Add(string kind, string name, long owner = 0, string resource = "", string bundle = "",
            string state = "owned", IEnumerable<string> dependencies = null)
        {
            long id = ++_next;
            _owners.Add(id, new AssetOwnershipRecord(id, kind, name, owner, resource, bundle, state, dependencies));
            int count = _counts.TryGetValue(kind, out int previous) ? previous + 1 : 1;
            _counts[kind] = count;
            _peaks[kind] = _peaks.TryGetValue(kind, out int peak) ? Math.Max(peak, count) : count;
            return id;
        }
        internal void Change(long id, long? owner = null, string state = null)
        {
            if (!_owners.TryGetValue(id, out var item)) { return; }
            _owners[id] = new AssetOwnershipRecord(id, item.Kind, item.Name, owner ?? item.OwnerId,
                item.ResourceKey, item.Bundle, state ?? item.State, item.Dependencies);
        }
        internal void Remove(long id)
        {
            if (_owners.TryGetValue(id, out var item)) { _owners.Remove(id); _counts[item.Kind]--; }
        }
        internal static double Now => (double)Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        internal void Finish(long id, string name, double queued, double started, Exception error)
        {
            string state = error == null ? "succeeded" : error is OperationCanceledException ? "cancelled" : "failed";
            if (error is OperationCanceledException) { Cancellations++; }
            else if (error != null) { Failures++; }
            if (_history.Count == 256) { _history.Dequeue(); }
            double now = Now;
            _history.Enqueue(new AssetOperationRecord(id, name, state, (started < 0 ? now : started) - queued,
                started < 0 ? 0 : now - started));
            Remove(id);
        }
        internal AssetOwnershipSnapshot Snapshot(AssetLoadScheduler scheduler)
            => new AssetOwnershipSnapshot(_generation, _owners.Values, _history, _peaks, SharedHits, Failures, Cancellations, scheduler);
    }
}
