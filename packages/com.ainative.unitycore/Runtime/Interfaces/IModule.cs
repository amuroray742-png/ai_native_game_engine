using AiNative.UnityCore.Exceptions;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.Interfaces
{
    public interface IModule
    {
        string GetModuleName();

        /// <summary>
        /// 
        /// </summary>
        void OnRegister(ICore core);
        /// <summary>
        /// 模块初始化
        /// </summary>
        /// <param name="core"></param>
        /// <returns></returns>
        UniTask<UniCoreException> Initialize(ICore core);
        /**
         * 依赖注入点
         */
        void AfterInitialize(ICore core);
        /**
         * 模块启动
         */
        UniTask<UniCoreException> Start(ICore core);
        /**
         * 启动后处理
         */
        void AfterStart(ICore core);
        /**
         * 模块关闭
         */
        void Shut(ICore core);
    }
}
