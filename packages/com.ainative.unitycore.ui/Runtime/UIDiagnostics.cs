namespace AiNative.UnityCore.UI
{
    /// <summary>Immutable counts for one captured UI state; not a rendering batch prediction.</summary>
    public sealed class UIDiagnostics
    {
        public int WindowCount { get; }
        public int PendingOperations { get; }
        public int PageDepth { get; }
        public int ModalCount { get; }
        public int ClosedCacheCount { get; }
        public int FailedCleanupCount { get; }
        public System.Collections.Generic.IReadOnlyList<UIWindowDiagnostic> Windows { get; }
        public int GraphicCount { get; }
        public int CanvasCount { get; }
        public int MaskCount { get; }
        public int RectMaskCount { get; }
        public string LastRecoveryError { get; }
        public System.Collections.Generic.IReadOnlyDictionary<UIWindowState, int> StateCounts { get; }

        internal UIDiagnostics(int windowCount, int pendingOperations, int pageDepth,
            int modalCount, int closedCacheCount, int failedCleanupCount,
            UIWindowDiagnostic[] windows = null, int graphics = 0, int canvases = 0, int masks = 0,
            int rectMasks = 0, string lastRecoveryError = null)
        {
            WindowCount = windowCount;
            PendingOperations = pendingOperations;
            PageDepth = pageDepth;
            ModalCount = modalCount;
            ClosedCacheCount = closedCacheCount;
            FailedCleanupCount = failedCleanupCount;
            Windows = System.Array.AsReadOnly(windows ?? System.Array.Empty<UIWindowDiagnostic>());
            var counts = new System.Collections.Generic.Dictionary<UIWindowState, int>();
            foreach (UIWindowState state in System.Enum.GetValues(typeof(UIWindowState))) { counts.Add(state, 0); }
            foreach (var window in Windows) { counts[window.State]++; }
            StateCounts = new System.Collections.ObjectModel.ReadOnlyDictionary<UIWindowState, int>(counts);
            GraphicCount = graphics;
            CanvasCount = canvases;
            MaskCount = masks;
            RectMaskCount = rectMasks;
            LastRecoveryError = lastRecoveryError;
        }
    }
}
