using System;

namespace AiNative.UnityCore.UI
{
    public sealed class UIOperationException : Exception
    {
        public bool Committed { get; }
        public UIHandle? Handle { get; }
        public UIHandle? CleanupHandle { get; }

        public UIOperationException(string message, Exception innerException, bool committed,
            UIHandle? handle = null, UIHandle? cleanupHandle = null) : base(message, innerException)
        {
            Committed = committed;
            Handle = handle;
            CleanupHandle = cleanupHandle;
        }
    }
}
