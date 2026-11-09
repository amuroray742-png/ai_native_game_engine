#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using AiNative.UnityCore.Attributes;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;

namespace AiNative.UnityCore
{
    internal sealed class ModuleManager
    {
        private readonly Dictionary<Type, IModule> _services = new Dictionary<Type, IModule>();
        private readonly List<IModule> _modules = new List<IModule>();
        private IModule? _registering;

        internal void Register(ICore core, IModule module)
        {
            if (module == null) { throw new ArgumentNullException(nameof(module)); }
            if (_registering != null) { throw new InvalidOperationException("Nested module registration is not allowed."); }
            if (_modules.Exists(item => ReferenceEquals(item, module)))
            {
                throw new InvalidOperationException("This module instance is already registered.");
            }
            var previous = new Dictionary<Type, IModule>(_services);
            _registering = module;
            try
            {
                module.OnRegister(core);
                _modules.Add(module);
            }
            catch
            {
                _services.Clear();
                foreach (var pair in previous) { _services.Add(pair.Key, pair.Value); }
                throw;
            }
            finally { _registering = null; }
        }

        internal void RegisterInterface<TService, TModule>(TModule module) where TModule : class, IModule, TService
        {
            if (!ReferenceEquals(_registering, module))
            {
                throw new InvalidOperationException("Declare interfaces for this module inside its OnRegister callback.");
            }
            if (_services.ContainsKey(typeof(TService)))
            {
                throw new UniCoreException($"Module interface {typeof(TService)} already exists!", ErrorCode.ErrorModuleSameInterfaceExists);
            }
            _services.Add(typeof(TService), module);
        }

        internal T Get<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var module)) { return (T)module; }
            throw new InvalidOperationException($"Required module interface {typeof(T)} is not registered.");
        }

        internal IModule[] GetStartOrder()
        {
            if (_registering != null) { throw new InvalidOperationException("Cannot start inside OnRegister."); }
            var ordered = new List<IModule>(_modules);
            ordered.Sort((left, right) =>
            {
                int leftOrder = left.GetType().GetCustomAttribute<ModuleOrderAttribute>()?.Order ?? 100;
                int rightOrder = right.GetType().GetCustomAttribute<ModuleOrderAttribute>()?.Order ?? 100;
                int comparison = leftOrder.CompareTo(rightOrder);
                return comparison != 0 ? comparison : _modules.FindIndex(item => ReferenceEquals(item, left))
                    .CompareTo(_modules.FindIndex(item => ReferenceEquals(item, right)));
            });
            return ordered.ToArray();
        }
    }
}
