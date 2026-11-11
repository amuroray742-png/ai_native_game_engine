using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AiNative.UnityCore.Assets
{
    public sealed class AssetDiagnostics
    {
        public IReadOnlyList<string> Bundles { get; }
        public IReadOnlyList<string> Resources { get; }
        public IReadOnlyList<string> Scopes { get; }
        public IReadOnlyList<string> Pools { get; }
        public IReadOnlyList<string> Errors { get; }
        public int PendingOperations { get; }
        public AssetOwnershipSnapshot Ownership { get; }
        internal AssetDiagnostics(List<string> bundles, List<string> resources, List<string> scopes, int pending,
            List<string> pools = null, List<string> errors = null, AssetOwnershipSnapshot ownership = null)
        {
            Bundles = new ReadOnlyCollection<string>(bundles);
            Resources = new ReadOnlyCollection<string>(resources);
            Scopes = new ReadOnlyCollection<string>(scopes);
            Pools = new ReadOnlyCollection<string>(pools ?? new List<string>());
            Errors = new ReadOnlyCollection<string>(errors ?? new List<string>());
            PendingOperations = pending;
            Ownership = ownership;
        }
    }
}
