#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.Consts;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore
{
    /// <summary>Unity 应用级服务的唯一入口，不拥有对局状态。只支持主线程调用，不提供跨线程检查或调度。</summary>
    public sealed class Core : ICore
    {
        private static Core? _instance;
        private static bool _exiting;
        private readonly ModuleManager _modules = new ModuleManager();
        private CoreRun? _run;
        private UniTaskCompletionSource? _closeCompletion;
        private bool _retired;
        private Action<string, UniCoreException>? _initException;
        private Action<string, UniCoreException>? _startException;

        public static Core Inst
        {
            get
            {
                if (_exiting) { throw new InvalidOperationException("UnityCore is exiting Play mode."); }
                return _instance ??= new Core();
            }
        }

        // Exit diagnostics must not reopen application services after retirement.
        public static Logger.Logger Log => _exiting ? new Logger.Logger() : Inst.Logger;
        public Logger.Logger Logger { get; } = new Logger.Logger();
        public ITimeMachine TimeMachine { get; } = new TimeMachine.TimeMachine();
        public CoreState State { get; private set; }
        public bool IsRunning => State == CoreState.Running;
#if UNITY_EDITOR || !AINATIVE_RELEASE
        public bool IsDevelopMode => true;
#else
        public bool IsDevelopMode => false;
#endif

        public GameObject? RootGameObject => _run?.Root;

        private Core() { }

        public ICore RegisterModule(IModule module)
        {
            EnsureStopped();
            _modules.Register(this, module);
            return this;
        }

        public ICore RegisterInterface<TService, TModule>(TModule module) where TModule : class, IModule, TService
        {
            EnsureStopped();
            _modules.RegisterInterface<TService, TModule>(module);
            return this;
        }

        public T GetModule<T>() where T : class
        {
            EnsureCurrent();
            return _modules.Get<T>();
        }

        public ICore OnModulesInitException(Action<string, UniCoreException> callback)
        {
            EnsureCurrent();
            _initException += callback ?? throw new ArgumentNullException(nameof(callback));
            return this;
        }

        public ICore OnModulesStartException(Action<string, UniCoreException> callback)
        {
            EnsureCurrent();
            _startException += callback ?? throw new ArgumentNullException(nameof(callback));
            return this;
        }

        public UniTask RunAsync(CancellationToken cancellationToken = default)
        {
            EnsureCurrent();
            if (State == CoreState.Running) { return UniTask.CompletedTask; }
            EnsureStopped();
            cancellationToken.ThrowIfCancellationRequested();
            var run = new CoreRun(_modules.GetStartOrder(), cancellationToken);
            _run = run;
            _closeCompletion = null;
            State = CoreState.Starting;
            RunOperationAsync(run).Forget();
            return run.Started.Task;
        }

        public UniTask CloseAsync()
        {
            if (_closeCompletion != null) { return _closeCompletion.Task; }
            if (_run == null) { return UniTask.CompletedTask; }
            var completion = new UniTaskCompletionSource();
            _closeCompletion = completion;
            State = CoreState.Closing;
            CloseOperationAsync(_run, completion).Forget();
            return completion.Task;
        }

        public async UniTask RestartAsync(CancellationToken cancellationToken = default)
        {
            EnsureCurrent();
            if (State == CoreState.Starting || State == CoreState.Closing)
            {
                throw new InvalidOperationException("Cannot restart during a lifecycle operation.");
            }
            await CloseAsync();
            await RunAsync(cancellationToken);
        }

        private async UniTask RunOperationAsync(CoreRun run)
        {
            IModule? startingModule = null;
            try
            {
                CreateRoot(run);
                Logger.Debug("[AiNative.UnityCore] Framework - Ver.{0}", FrameworkConst.FrameworkVersion);
                var tasks = new UniTask<Exception?>[run.Modules.Length];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = InitializeAsync(run, run.Modules[i]);
                }

                // Each task captures its failure, so WhenAll settles every module before cleanup.
                Exception?[] results = await UniTask.WhenAll(tasks);
                var failures = new List<Exception>();
                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i] is Exception error)
                    {
                        failures.Add(error);
                        Notify(_initException, run.Modules[i], error, failures);
                    }
                }
                if (failures.Count != 0)
                {
                    await FailStartupAsync(run, failures);
                    return;
                }
                EnsureStarting(run);
                foreach (var module in run.Modules)
                {
                    module.AfterInitialize(this);
                    EnsureStarting(run);
                }
                foreach (var module in run.Modules)
                {
                    startingModule = module;
                    UniCoreException? error = await module.Start(this, run.Token);
                    startingModule = null;
                    if (error != null)
                    {
                        failures.Add(error);
                        Notify(_startException, module, error, failures);
                        await FailStartupAsync(run, failures);
                        return;
                    }
                    EnsureStarting(run);
                }
                foreach (var module in run.Modules)
                {
                    module.AfterStart(this);
                    EnsureStarting(run);
                }
                run.FinishStartup();
                State = CoreState.Running;
                TimeMachine.StartFrameLogicTimer();
                run.Alpha!.enabled = true;
                run.Omega!.enabled = true;
                run.Started.TrySetResult();
            }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                if (startingModule != null)
                {
                    Notify(_startException, startingModule, error, failures);
                }
                await FailStartupAsync(run, failures);
            }
            finally { run.Settled.TrySetResult(); }
        }

        private async UniTask FailStartupAsync(CoreRun run, List<Exception> failures)
        {
            StopDispatch(run);
            run.Cancel();
            await CleanupAsync(run);
            if (State != CoreState.Closing) { State = CoreState.Faulted; }
            failures.AddRange(run.CleanupErrors);
            if (run.CleanupErrors.Count == 0 && failures.TrueForAll(error => error is OperationCanceledException))
            {
                run.Started.TrySetCanceled(run.Token);
            }
            else { run.Started.TrySetException(Combine(failures)); }
        }

        private async UniTask<Exception?> InitializeAsync(CoreRun run, IModule module)
        {
            try
            {
                run.Token.ThrowIfCancellationRequested();
                run.Entered.Add(module);
                return await module.Initialize(this, run.Token);
            }
            catch (Exception error) { return error; }
        }

        private async UniTask CloseOperationAsync(CoreRun run, UniTaskCompletionSource completion)
        {
            try
            {
                StopDispatch(run);
                run.Cancel();
                await run.Settled.Task;
                await CleanupAsync(run);
                _run = null;
                State = run.CleanupErrors.Count == 0 ? CoreState.Stopped : CoreState.Faulted;
                if (run.CleanupErrors.Count == 0) { completion.TrySetResult(); }
                else { completion.TrySetException(Combine(run.CleanupErrors)); }
            }
            catch (Exception error)
            {
                State = CoreState.Faulted;
                completion.TrySetException(error);
            }
        }

        private void CreateRoot(CoreRun run)
        {
            run.Root = new GameObject(FrameworkConst.FrameworkRootGameObject);
            UnityEngine.Object.DontDestroyOnLoad(run.Root);
            run.Lifetime = run.Root.AddComponent<CoreLifetime>();
            run.Lifetime.Bind(this);
            run.Alpha = run.Root.AddComponent<AlphaTimeEngine>();
            run.Omega = run.Root.AddComponent<OmegaTimeEngine>();
            run.Alpha.enabled = false;
            run.Omega.enabled = false;
            run.Alpha.SetTimeMachine(TimeMachine);
            run.Omega.SetTimeMachine(TimeMachine);
            TimeMachine.StartFrameLogicTimer();
        }

        private static void StopDispatch(CoreRun run)
        {
            if (run.Alpha != null) { run.Alpha.enabled = false; }
            if (run.Omega != null) { run.Omega.enabled = false; }
        }

        private async UniTask CleanupAsync(CoreRun run)
        {
            if (run.Cleaned) { return; }
            if (run.Cleaning) { await run.CleanupCompleted.Task; return; }
            run.Cleaning = true;
            for (int i = run.Entered.Count - 1; i >= 0; i--)
            {
                if (!_retired && run.Entered[i] is IAsyncShutdownModule asynchronous)
                {
                    try { await asynchronous.ShutdownAsync(this, run.ForcedExit.Token).AttachExternalCancellation(run.ForcedExit.Token); }
                    catch (OperationCanceledException) when (_retired) { }
                    catch (Exception error) { run.CleanupErrors.Add(error); }
                }
                try { run.Entered[i].Shut(this); }
                catch (Exception error) { run.CleanupErrors.Add(error); }
            }
            TimeMachine.ClearAll();
            if (run.Lifetime != null) { run.Lifetime.Unbind(); }
            if (run.Root != null)
            {
                run.Root.SetActive(false);
                UnityEngine.Object.Destroy(run.Root);
            }
            run.Root = null;
            run.Cleaned = true;
            run.DisposeCancellation();
            run.CleanupCompleted.TrySetResult();
        }

        private static void Notify(Action<string, UniCoreException>? callbacks, IModule module,
            Exception error, List<Exception> failures)
        {
            if (callbacks == null || error is OperationCanceledException) { return; }
            foreach (Action<string, UniCoreException> callback in callbacks.GetInvocationList())
            {
                try
                {
                    callback(module.GetModuleName(), error as UniCoreException
                        ?? new UniCoreException("Module lifecycle failed.", error));
                }
                catch (Exception notificationError) { failures.Add(notificationError); }
            }
        }

        private static Exception Combine(List<Exception> errors)
        {
            return errors.Count == 1 ? errors[0] : new AggregateException("UnityCore lifecycle failed.", errors);
        }

        private void EnsureStarting(CoreRun run)
        {
            run.Token.ThrowIfCancellationRequested();
            if (_retired || !ReferenceEquals(_run, run) || State != CoreState.Starting)
            {
                throw new OperationCanceledException(run.Token);
            }
        }

        private void EnsureCurrent()
        {
            if (_retired) { throw new ObjectDisposedException(nameof(Core), "This Core belongs to a previous Play session."); }
        }

        private void EnsureStopped()
        {
            EnsureCurrent();
            if (State != CoreState.Stopped) { throw new InvalidOperationException($"Core must be stopped, current state: {State}."); }
        }

        internal void OnRootDestroyed()
        {
            CloseAsync().Forget(error => Logger.Error(error));
        }

        internal static void RetireCurrent()
        {
            _exiting = true;
            Core? previous = _instance;
            _instance = null;
            if (previous == null) { return; }
            previous._retired = true;
            if (previous._run != null && !previous._run.Cleaned)
            {
                try { previous._run.ForcedExit.Cancel(); }
                catch (Exception error) { previous._run.CleanupErrors.Add(error); }
            }
            if (previous._closeCompletion == null)
            {
                previous.CloseAsync().Forget(error => previous.Logger.Error(error));
            }
            previous._initException = null;
            previous._startException = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay()
        {
            RetireCurrent();
            _exiting = false;
        }
    }
}
