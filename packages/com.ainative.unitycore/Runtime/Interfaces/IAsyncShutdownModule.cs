using System.Threading;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.Interfaces
{
    /// <summary>Optional main-thread draining before Shut. The token signals forced retirement, not normal shutdown.
    /// Implementations must isolate and reclaim late results after cancellation; Shut remains synchronous and idempotent.</summary>
    public interface IAsyncShutdownModule
    {
        UniTask ShutdownAsync(ICore core, CancellationToken forcedExit);
    }
}
