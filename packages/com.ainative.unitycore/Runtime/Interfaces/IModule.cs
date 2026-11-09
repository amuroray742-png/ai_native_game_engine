#nullable enable
using System.Threading;
using AiNative.UnityCore.Exceptions;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.Interfaces
{
    public interface IModule
    {
        string GetModuleName();
        /// <summary>只声明接口映射，不获取运行资源或读取其他模块。</summary>
        void OnRegister(ICore core);
        /// <summary>
        /// 并行初始化自身资源，成功返回 null。响应取消并释放尚未交付的资源；
        /// 取消收尾不能依赖后续 PlayerLoop 帧，完成与生命周期回调必须在主线程。
        /// </summary>
        UniTask<UniCoreException?> Initialize(ICore core, CancellationToken cancellationToken);
        /// <summary>所有初始化成功后按顺序绑定接口；运行方法使用已保存的依赖。</summary>
        void AfterInitialize(ICore core);
        /// <summary>按顺序启动；成功返回 null，取消约定与 Initialize 相同。</summary>
        UniTask<UniCoreException?> Start(ICore core, CancellationToken cancellationToken);
        void AfterStart(ICore core);
        /// <summary>逆序同步释放；必须支持部分初始化、重复释放和之后的重新初始化。</summary>
        void Shut(ICore core);
    }
}
