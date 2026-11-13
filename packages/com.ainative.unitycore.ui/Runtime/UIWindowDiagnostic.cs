using System.Collections.Generic;

namespace AiNative.UnityCore.UI
{
    public enum UIWindowState { Opening, Entering, Visible, Covered, Exiting, CachedHidden, Releasing, CleanupFailed }

    public sealed class UIWindowDiagnostic
    {
        public string ViewType { get; }
        public UIHandle Handle { get; }
        public UIWindowState State { get; }
        public bool IsAlive { get; }
        public IReadOnlyList<UIComponentDiagnostic> Components { get; }
        internal UIWindowDiagnostic(string type, UIHandle handle, UIWindowState state, bool alive, UIComponentDiagnostic[] components)
        { ViewType = type; Handle = handle; State = state; IsAlive = alive; Components = System.Array.AsReadOnly(components); }
    }

    public sealed class UIComponentDiagnostic
    {
        public string Type { get; }
        public string ObjectName { get; }
        public int InstanceId { get; }
        public int MaterialId { get; }
        public int TextureId { get; }
        public bool OverrideSorting { get; }
        public string MaterialOrigin => "Unknown";
        internal UIComponentDiagnostic(string type, string objectName, int id, int material, int texture, bool sorting)
        { Type = type; ObjectName = objectName; InstanceId = id; MaterialId = material; TextureId = texture; OverrideSorting = sorting; }
    }
}
