using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        internal UIDiagnostics Diagnostics()
        {
            var windows = new List<UIWindowDiagnostic>();
            int failed = 0;
            foreach (var record in Windows.Values)
            {
                if (record.CleanupFailed) { failed++; }
                var components = new List<UIComponentDiagnostic>();
                if (record.View != null)
                {
                    foreach (var component in record.View.GetComponentsInChildren<Component>(true))
                    {
                        if (!(component is Graphic) && !(component is Canvas) && !(component is Mask) && !(component is RectMask2D)) { continue; }
                        var graphic = component as Graphic;
                        // Do not evaluate materialForRendering: stencil modifiers may allocate.
                        var material = graphic != null ? graphic.material : null;
                        var texture = graphic != null ? graphic.mainTexture : null;
                        components.Add(new UIComponentDiagnostic(component.GetType().FullName, component.gameObject.name,
                            component.GetInstanceID(), material != null ? material.GetInstanceID() : 0,
                            texture != null ? texture.GetInstanceID() : 0,
                            component is Canvas canvas && canvas.overrideSorting));
                    }
                }
                var state = record.CleanupFailed ? UIWindowState.CleanupFailed : record.Releasing ? UIWindowState.Releasing :
                    record.Transitioning ? (record.Opening ? UIWindowState.Entering : UIWindowState.Exiting) :
                    record.Closing != null && record.Closing.Task.Status == Cysharp.Threading.Tasks.UniTaskStatus.Pending ? UIWindowState.Exiting :
                    record.Opening ? UIWindowState.Opening : record.Cached ? UIWindowState.CachedHidden :
                    record.Visible ? UIWindowState.Visible : UIWindowState.Covered;
                windows.Add(new UIWindowDiagnostic(record.ViewType.FullName, record.Handle, state,
                    record.Instance != null && record.Instance.IsAlive && record.View != null, components.ToArray()));
            }
            var root = Root.Object;
            return new UIDiagnostics(Windows.Count, Pending, Navigation.PageDepth, Navigation.ModalCount, Cache.Count, failed,
                windows.ToArray(), root != null ? root.GetComponentsInChildren<Graphic>(true).Length : 0,
                root != null ? root.GetComponentsInChildren<Canvas>(true).Length : 0,
                root != null ? root.GetComponentsInChildren<Mask>(true).Length : 0,
                root != null ? root.GetComponentsInChildren<RectMask2D>(true).Length : 0, LastRecoveryError);
        }
    }
}
