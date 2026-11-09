#nullable enable
using System;
using System.Threading;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.Interfaces
{
    /// <summary>Unity 应用服务的装配入口。生命周期与模块回调均在 Unity 主线程执行。</summary>
    public interface ICore
    {
        CoreState State { get; }
        bool IsRunning { get; }
        bool IsDevelopMode { get; }
        Logger.Logger Logger { get; }
        ITimeMachine TimeMachine { get; }

        /// <summary>仅停止时注册；OnRegister 失败会撤销本次注册的接口。</summary>
        ICore RegisterModule(IModule module);
        ICore RegisterInterface<TService, TModule>(TModule module) where TModule : class, IModule, TService;

        /// <summary>仅用于装配与 AfterInitialize 依赖绑定。缺失接口立即失败。</summary>
        T GetModule<T>() where T : class;
        ICore OnModulesInitException(Action<string, UniCoreException> callback);
        ICore OnModulesStartException(Action<string, UniCoreException> callback);

        /// <summary>运行时幂等；启动中重复调用失败。参数仅取消启动，成功后由 CloseAsync 控制生命周期。</summary>
        UniTask RunAsync(CancellationToken cancellationToken = default);
        /// <summary>立即停派发并取消启动，等待收尾；保留模块注册，重复关闭等待同一次操作。</summary>
        UniTask CloseAsync();
        /// <summary>关闭成功后重新初始化同一批实例。启动或关闭期间不得重启。</summary>
        UniTask RestartAsync(CancellationToken cancellationToken = default);
    }
}
