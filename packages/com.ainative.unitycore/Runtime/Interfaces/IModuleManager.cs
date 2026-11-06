using System.Collections.Generic;

namespace AiNative.UnityCore.Interfaces
{
    public interface IModuleManager
    {
        T Get<T>();
        void Register<T>(ICore core, T moduleInst) where T : IModule;
        void RegisterInterface<T1, T2>(T2 moduleInst) where T2 : IModule, T1;
        List<IModule> ModuleList { get; }
        void ClearModuleList();
    }
}
