using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.Assets
{
    internal sealed class InstanceLifetime : MonoBehaviour
    {
        internal readonly UniTaskCompletionSource Completion = new UniTaskCompletionSource();
        private AssetLease<GameObject> _lease;
        internal void Bind(AssetLease<GameObject> lease)
        {
            _lease = lease;
            ObserveDestructionAsync(gameObject).Forget(Debug.LogException);
        }
        // Unity does not send OnDestroy for objects that have never been active.
        private async UniTask ObserveDestructionAsync(GameObject instance)
        {
            await UniTask.WaitUntil(() => instance == null, PlayerLoopTiming.LastPostLateUpdate);
            Release();
        }
        // OnDestroy is too early: other components can still use dependencies in their OnDestroy.
        // Normal destruction releases only after the GameObject becomes null; ForceDestroy releases
        // explicitly after DestroyImmediate returns, when all component callbacks have completed.
        internal void Release()
        {
            _lease?.Dispose();
            _lease = null;
            Completion.TrySetResult();
        }
    }
}
