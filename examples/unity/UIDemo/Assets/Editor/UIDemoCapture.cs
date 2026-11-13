using System;
using System.Linq;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AiNative.UIDemo.Editor
{
    public static class UIDemoCapture
    {
        [MenuItem("UIDemo/Pause gallery transition for screenshot")]
        public static void PauseGalleryTransition() { PauseAsync().Forget(Debug.LogException); }

        private static async UniTask PauseAsync()
        {
            var host = UnityEngine.Object.FindFirstObjectByType<UIDemoBootstrap>();
            if (!EditorApplication.isPlaying || host == null || host.Busy || host.UI.GetDiagnostics().PageDepth != 1)
            { throw new InvalidOperationException("Enter Play and return to Home before capturing the transition."); }
            var opening = host.ExecuteAsync(DemoAction.Next).Preserve();
            await UniTask.WaitUntil(() => host.UI.GetDiagnostics().Windows.Any(x => x.State == UIWindowState.Entering)
                || opening.Status != UniTaskStatus.Pending);
            if (opening.Status == UniTaskStatus.Pending)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(0.1), ignoreTimeScale: true);
                EditorApplication.isPaused = true;
            }
            // Resume Play after the screen capture. Errors still propagate to Console.
            await opening;
        }
    }
}
