using System;
using System.Collections.Generic;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;

namespace AiNative.UnityCore
{
    public class ModuleManager : IModuleManager
    {
        private Dictionary<Type, IModule> _moduleDict = new Dictionary<Type, IModule>();
        private List<IModule> _moduleList = new List<IModule>();
        public List<IModule> ModuleList => _moduleList;

        public void ClearModuleList()
        {
            _moduleList.Clear();
            _moduleDict.Clear();
        }

        public T Get<T>()
        {
            Type t = typeof(T);
            if (_moduleDict.TryGetValue(t, out var module))
            {
                return (T)module;
            }

            return default;
        }

        public void Register<T>(ICore core, T moduleInst) where T : IModule
        {
            moduleInst.OnRegister(core);
        }

        public void RegisterInterface<T1, T2>(T2 moduleInst) where T2 : IModule, T1
        {
            Type type = typeof(T1);
            if (!_moduleDict.TryGetValue(type, out var mod))
            {
                _moduleDict[type] = moduleInst;
                // 不能重复添加
                if (!_moduleList.Contains(moduleInst))
                {
                    _moduleList.Add(moduleInst);
                }
            }
            else
            {
                throw new UniCoreException($"Module {type} already exists!", ErrorCode.ErrorModuleSameInterfaceExists);
            }
        }
    }
}
