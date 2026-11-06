using System;
using AiNative.UnityCore.Exceptions;
using Cysharp.Threading.Tasks;

namespace AiNative.UnityCore.Interfaces
{
    public interface ICore
    {
        bool IsRunning { get; }
        bool IsDevelopMode { get; }

        /// <summary>
        /// 注册模块
        /// </summary>
        /// <param name="module"></param>
        /// <returns></returns>
        ICore RegisterModule(IModule module);
        ICore RegisterInterface<T1, T2>(T2 module) where T2 : IModule, T1;
        /// <summary>
        /// 获取模块实例
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="moduleName"></param>
        /// <returns></returns>
        T GetModule<T>();
        ICore OnModulesInitException(Action<string, UniCoreException> callback);
        ICore OnModulesStartException(Action<string, UniCoreException> callback);
        /// <summary>
        /// 启动入口
        /// </summary>
        /// <returns></returns>
        UniTask RunAsync();

        /// <summary>
        /// 结束出口
        /// </summary>
        void Close();

        /// <summary>
        /// 重启
        /// </summary>
        /// <returns></returns>
        UniTask Restart();
    }
}
