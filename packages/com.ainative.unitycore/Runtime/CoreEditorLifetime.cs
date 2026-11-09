#if UNITY_EDITOR
using UnityEditor;

namespace AiNative.UnityCore
{
    [InitializeOnLoad]
    internal static class CoreEditorLifetime
    {
        static CoreEditorLifetime()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Core.RetireCurrent;
            AssemblyReloadEvents.beforeAssemblyReload += Core.RetireCurrent;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) { Core.RetireCurrent(); }
        }
    }
}
#endif
