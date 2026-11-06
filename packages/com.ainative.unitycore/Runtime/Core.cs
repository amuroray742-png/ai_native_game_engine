using System;
using System.Reflection;
using AiNative.UnityCore.Attributes;
using AiNative.UnityCore.Consts;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using AiNative.UnityCore.Single;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;


namespace AiNative.UnityCore
{
    public class Core : Singleton<Core>, ICore
    {
        private IModuleManager _moduleManager;
        private bool _isRunning;
        private bool _isDevelopMode;
        private GameObject? _rootGameObject;
        private Action<string, UniCoreException>? _modulesInitExceptionAction;
        private Action<string, UniCoreException>? _modulesStartExceptionAction;
        public GameObject? RootGameObject => _rootGameObject;
        public bool IsRunning => _isRunning;
        public bool IsDevelopMode => _isDevelopMode;
        public static Logger.Logger Log => Logger.Logger.Inst;
        private ITimeMachine _timeMachine;
        public ITimeMachine TimeMachine => _timeMachine;

        public ICore OnModulesInitException(Action<string, UniCoreException> callback)
        {
            _modulesInitExceptionAction += callback;
            return this;
        }

        public ICore OnModulesStartException(Action<string, UniCoreException> callback)
        {
            _modulesStartExceptionAction += callback;
            return this;
        }

        protected Core()
        {
            _moduleManager = new ModuleManager();
            _timeMachine = new TimeMachine.TimeMachine();
        }

        public T GetModule<T>()
        {
            return _moduleManager.Get<T>();
        }

        public ICore RegisterModule(IModule module)
        {
            _moduleManager.Register(this, module);
            return this;
        }

        public ICore RegisterInterface<T1, T2>(T2 module) where T2 : IModule, T1
        {
            _moduleManager.RegisterInterface<T1, T2>(module);
            return this;
        }

        public async UniTask RunAsync()
        {
            if (_isRunning)
            {
                return;
            }

#if UNITY_EDITOR || !WEPIE_RELEASE
            _isDevelopMode = true;
#else
            _isDevelopMode = false;
#endif

            Log.Debug("[AiNative.UnityCore] Framework - Ver.{0}", FrameworkConst.FrameworkVersion);
            // 设置屏幕不休眠
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            _rootGameObject = GameObjectExtend.FindOrCreateGameObject(FrameworkConst.FrameworkRootGameObject);
            _rootGameObject.transform.position = new Vector3(-9999, -9999, -9999);
            UnityEngine.Object.DontDestroyOnLoad(_rootGameObject);

            var moduleList = _moduleManager.ModuleList;
            int GetModuleOrder(ref IModule mod)
            {
                Type type = mod.GetType();
                var attr = type.GetCustomAttribute<ModuleOrderAttribute>();
                if (attr != null)
                {
                    return attr.Order;
                }
                return 100;
            }

            moduleList.Sort((x, y) => GetModuleOrder(ref x).CompareTo(GetModuleOrder(ref y)));

            UniTask<UniCoreException>[] arrInitTasks = new UniTask<UniCoreException>[moduleList.Count];
            for (int i = 0; i < moduleList.Count; i++)
            {
                Log.Debug((moduleList: moduleList, i: i), static logState => string.Format("    [Module Init]:{0}", logState.moduleList[logState.i].GetModuleName()));
                arrInitTasks[i] = moduleList[i].Initialize(this);
            }

            // 可以并行初始化模块，因为初始化时不依赖其他模块
            await UniTask.WhenAll(arrInitTasks);

            for (int i = 0; i < moduleList.Count; i++)
            {
                var exception = arrInitTasks[i].GetAwaiter().GetResult();
                if (exception != null)
                {
                    if (_modulesInitExceptionAction != null && _modulesInitExceptionAction.GetInvocationList().Length > 0)
                    {
                        _modulesInitExceptionAction.Invoke(moduleList[i].GetModuleName(), exception);
                    }
                    else
                    {
                        Log.Error((moduleList: moduleList, i: i), static logState => string.Format("[Wepie.Core] Exception when init module \"{0}\"", logState.moduleList[logState.i].GetModuleName()));
                        throw exception;
                    }
                }
            }

            // 依赖注入点，顺序串行执行
            foreach (var mod in moduleList)
            {
                mod.AfterInitialize(this);
            }


            // 模块启动，顺序串行执行
            foreach (var m in moduleList)
            {
                Log.Debug(m, static logState => string.Format("    [Module Start]:{0}", logState.GetModuleName()));
                var exception = await m.Start(this);
                if (exception != null)
                {
                    if (_modulesStartExceptionAction != null && _modulesStartExceptionAction.GetInvocationList().Length > 0)
                    {
                        _modulesStartExceptionAction.Invoke(m.GetModuleName(), exception);
                    }
                    else
                    {
                        Log.Error(m, static logState => string.Format("[Wepie.Core] Exception when start module \"{0}\"", logState.GetModuleName()));
                        throw exception;
                    }
                }
            }

            // 启动后处理，顺序串行执行
            foreach (var mod in moduleList)
            {
                mod.AfterStart(this);
            }

            _isRunning = true;
            Log.Debug("Framework startup finish.");
        }

        public void Close()
        {
            if (!_isRunning)
            {
                return;
            }

            Log.Debug("Framework is closing...");
            var moduleList = this._moduleManager.ModuleList;
            // 关闭要和启动顺序相反
            for (int i = moduleList.Count - 1; i >= 0; i--)
            {
                moduleList[i].Shut(this);
            }

            _moduleManager.ClearModuleList();
            _isRunning = false;
        }

        public async UniTask Restart()
        {
            Close();
            await RunAsync();
        }
    }
}
