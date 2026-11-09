#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.Attributes;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Tests
{
    public sealed class CoreLifecycleTests
    {
        private Core _core = null!;

        [SetUp]
        public void SetUp()
        {
            Core.ResetForPlay();
            _core = Core.Inst;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            await CaptureFailure(_core.CloseAsync());
            Core.ResetForPlay();
            await UniTask.Yield();
        });

        [UnityTest]
        public IEnumerator DefaultLoggerAndPooledInitializationStartTheDrivers() => UniTask.ToCoroutine(async () =>
        {
            var first = new Module("first") { Init = YieldInitialization };
            var second = new Module("second") { Init = YieldInitialization };
            _core.RegisterModule(first).RegisterModule(second);
            int updates = 0, late = 0, fixedUpdates = 0, omega = 0, timers = 0;
            _core.TimeMachine.RegisterUpdate(_ => updates++, "update");
            _core.TimeMachine.RegisterLateUpdate(_ => late++, "late");
            _core.TimeMachine.RegisterFixedUpdate(_ => fixedUpdates++, "fixed");
            _core.TimeMachine.RegisterOmegaLateUpdate(_ => omega++, "omega");
            _core.TimeMachine.RegisterTimer(_ => timers++, 0.001f, "timer");
            UniTask start = _core.RunAsync();
            Assert.AreEqual(CoreState.Starting, _core.State);
            Assert.False(_core.RootGameObject!.GetComponent<AlphaTimeEngine>().enabled);
            Assert.Throws<InvalidOperationException>(() => _core.RunAsync());
            await start;
            Assert.AreSame(_core.Logger, Core.Log);
            Assert.AreEqual(1, first.Starts);
            Assert.AreEqual(1, second.Starts);
            await _core.RunAsync();
            await UniTask.Delay(100);
            Assert.Greater(updates, 0);
            Assert.Greater(late, 0);
            Assert.Greater(fixedUpdates, 0);
            Assert.Greater(omega, 0);
            Assert.Greater(timers, 0);
            await _core.CloseAsync();
            int stopped = updates;
            await UniTask.Yield();
            Assert.AreEqual(stopped, updates);
            Assert.IsNull(_core.RootGameObject);
        });

        [UnityTest]
        public IEnumerator EveryStartupFailureCleansBothEnteredModules() => UniTask.ToCoroutine(async () =>
        {
            foreach (string phase in new[] { "initialize-return", "initialize-throw", "binding", "start-return", "start-throw", "after-start" })
            {
                Core.ResetForPlay();
                _core = Core.Inst;
                var trace = new List<string>();
                var first = new Module("first", trace);
                var second = new Module("second", trace);
                ConfigureFailure(second, phase);
                _core.RegisterModule(first).RegisterModule(second);
                Exception? failure = await CaptureFailure(_core.RunAsync());
                Assert.NotNull(failure, phase);
                Assert.False(_core.IsRunning, phase);
                Assert.AreEqual(1, first.Shuts, phase);
                Assert.AreEqual(1, second.Shuts, phase);
                CollectionAssert.AreEqual(new[] { "second", "first" }, trace, phase);
                if (phase.StartsWith("initialize") || phase == "binding") { Assert.AreEqual(0, first.Starts); }
                await _core.CloseAsync();
                Assert.AreEqual(CoreState.Stopped, _core.State);
                Assert.AreEqual(1, second.Shuts);
                Assert.IsNull(_core.RootGameObject);
                await UniTask.Yield();
            }
        });

        [UnityTest]
        public IEnumerator NotificationExceptionsCannotTurnFailureIntoSuccessOrSkipCleanup() => UniTask.ToCoroutine(async () =>
        {
            foreach (string phase in new[] { "initialize-return", "start-return", "start-throw" })
            {
                Core.ResetForPlay();
                _core = Core.Inst;
                var module = new Module("failing");
                ConfigureFailure(module, phase);
                int notifications = 0;
                Action<string, UniCoreException> observer = (_, __) => throw new InvalidOperationException("observer");
                Action<string, UniCoreException> nextObserver = (_, __) => notifications++;
                if (phase == "initialize-return")
                {
                    _core.OnModulesInitException(observer).OnModulesInitException(nextObserver);
                }
                else
                {
                    _core.OnModulesStartException(observer).OnModulesStartException(nextObserver);
                }
                _core.RegisterModule(module);
                Exception? failure = await CaptureFailure(_core.RunAsync());
                Assert.IsInstanceOf<AggregateException>(failure, phase);
                Assert.AreEqual(2, ((AggregateException)failure!).InnerExceptions.Count, phase);
                Assert.AreEqual(1, notifications, phase);
                Assert.AreEqual(1, module.Shuts, phase);
                Assert.False(_core.IsRunning, phase);
            }
        });

        [UnityTest]
        public IEnumerator ReturnedModuleErrorPreservesItsCodeAndStopsLaterStages() => UniTask.ToCoroutine(async () =>
        {
            foreach (bool failInitialization in new[] { true, false })
            {
                Core.ResetForPlay();
                _core = Core.Inst;
                var expected = new UniCoreException("Required configuration is missing.", 401);
                var module = new Module("failing");
                var later = new Module("later");
                UniCoreException? reported = null;
                Action<string, UniCoreException> observer = (name, error) =>
                {
                    Assert.AreEqual("failing", name);
                    Assert.AreEqual(0, module.Shuts);
                    reported = error;
                };
                if (failInitialization)
                {
                    module.Init = (_, __) => UniTask.FromResult<UniCoreException?>(expected);
                    _core.OnModulesInitException(observer);
                }
                else
                {
                    module.StartAction = (_, __) => UniTask.FromResult<UniCoreException?>(expected);
                    _core.OnModulesStartException(observer);
                }
                _core.RegisterModule(module).RegisterModule(later);
                Exception? failure = await CaptureFailure(_core.RunAsync());
                Assert.AreSame(expected, failure);
                Assert.AreSame(expected, reported);
                Assert.AreEqual(401, ((UniCoreException)failure!).ErrorCode);
                Assert.AreEqual(0, later.Starts);
                Assert.AreEqual(1, module.Shuts);
                Assert.AreEqual(1, later.Shuts);
                Assert.AreEqual(CoreState.Faulted, _core.State);
            }
        });

        [UnityTest]
        public IEnumerator CloseDuringInitializationCancelsAndSharesCleanup() => UniTask.ToCoroutine(async () =>
        {
            var module = new Module("pending") { Init = WaitForCancellation };
            var second = new Module("second") { Init = WaitForCancellation };
            _core.RegisterModule(module).RegisterModule(second);
            UniTask startup = _core.RunAsync();
            UniTask firstClose = _core.CloseAsync();
            UniTask secondClose = _core.CloseAsync();
            await firstClose;
            await secondClose;
            Assert.IsInstanceOf<OperationCanceledException>(await CaptureFailure(startup));
            Assert.AreEqual(1, module.Shuts);
            Assert.AreEqual(1, second.Shuts);
            Assert.AreEqual(0, module.Starts);
            Assert.AreEqual(CoreState.Stopped, _core.State);
        });

        [UnityTest]
        public IEnumerator CloseContinuationCanStartAnotherRunBeforeCloseReturns() => UniTask.ToCoroutine(async () =>
        {
            var module = new Module("reentrant");
            UniTask followUp = UniTask.CompletedTask;
            module.OnShut = () =>
            {
                module.OnShut = null;
                followUp = StartAfterClose(_core);
            };
            _core.RegisterModule(module);
            await _core.RunAsync();
            await _core.CloseAsync();
            await followUp;
            Assert.True(_core.IsRunning);
            Assert.AreEqual(2, module.Starts);
            Assert.AreEqual(1, module.Shuts);
        });

        [UnityTest]
        public IEnumerator ExternalCancellationOnlyControlsStartup() => UniTask.ToCoroutine(async () =>
        {
            CancellationToken lifetime = default;
            var module = new Module("lifetime")
            {
                Init = (_, token) => { lifetime = token; return UniTask.FromResult<UniCoreException?>(null); }
            };
            _core.RegisterModule(module);
            using (var external = new CancellationTokenSource())
            {
                await _core.RunAsync(external.Token);
                external.Cancel();
                Assert.False(lifetime.IsCancellationRequested);
                Assert.True(_core.IsRunning);
                await _core.CloseAsync();
                Assert.True(lifetime.IsCancellationRequested);
                Assert.AreEqual(1, module.Shuts);
            }
        });

        [UnityTest]
        public IEnumerator ExternalCancellationSettlesAllPendingInitializers() => UniTask.ToCoroutine(async () =>
        {
            var first = new Module("first") { Init = WaitForCancellation };
            var second = new Module("second") { Init = WaitForCancellation };
            _core.RegisterModule(first).RegisterModule(second);
            using (var external = new CancellationTokenSource())
            {
                UniTask startup = _core.RunAsync(external.Token);
                external.Cancel();
                Assert.AreEqual(UniTaskStatus.Canceled, startup.Status);
                Assert.IsInstanceOf<OperationCanceledException>(await CaptureFailure(startup));
                Assert.AreEqual(1, first.Shuts);
                Assert.AreEqual(1, second.Shuts);
                Assert.False(_core.IsRunning);
                await _core.CloseAsync();
                Assert.AreEqual(CoreState.Stopped, _core.State);
            }
        });

        [UnityTest]
        public IEnumerator FailureWaitsForAllInitializersBeforeShuttingAnyModule() => UniTask.ToCoroutine(async () =>
        {
            var pending = new UniTaskCompletionSource<UniCoreException?>();
            var first = new Module("first") { Init = (_, __) => UniTask.FromResult<UniCoreException?>(new UniCoreException("fail")) };
            var second = new Module("second") { Init = (_, __) => pending.Task };
            _core.RegisterModule(first).RegisterModule(second);
            UniTask startup = _core.RunAsync();
            await UniTask.Yield();
            Assert.AreEqual(0, first.Shuts);
            Assert.AreEqual(0, second.Shuts);
            pending.TrySetResult(null);
            Assert.NotNull(await CaptureFailure(startup));
            Assert.AreEqual(1, first.Shuts);
            Assert.AreEqual(1, second.Shuts);
        });

        [UnityTest]
        public IEnumerator RestartPreservesInstancesAndRegistrationOrder() => UniTask.ToCoroutine(async () =>
        {
            var trace = new List<string>();
            var first = new Module("first", trace);
            var second = new Module("second", trace);
            first.Register = core => core.RegisterInterface<IService, Module>(first);
            _core.RegisterModule(first).RegisterModule(second);
            await _core.RunAsync();
            await _core.RestartAsync();
            Assert.AreSame(first, _core.GetModule<IService>());
            Assert.AreEqual(2, first.Initializations);
            Assert.AreEqual(2, first.Starts);
            Assert.AreEqual(1, first.Shuts);
            CollectionAssert.AreEqual(new[] { "second", "first" }, trace);
            Assert.True(_core.IsRunning);
        });

        [UnityTest]
        public IEnumerator ShutdownFailureStillReleasesOtherModulesAndStopsRestart() => UniTask.ToCoroutine(async () =>
        {
            var first = new Module("first");
            var second = new Module("second") { OnShut = () => throw new InvalidOperationException("shut") };
            _core.RegisterModule(first).RegisterModule(second);
            await _core.RunAsync();
            Assert.NotNull(await CaptureFailure(_core.RestartAsync()));
            Assert.AreEqual(1, first.Shuts);
            Assert.AreEqual(1, second.Shuts);
            Assert.AreEqual(1, first.Starts);
            Assert.AreEqual(CoreState.Faulted, _core.State);
            Assert.IsNull(_core.RootGameObject);
            Assert.NotNull(await CaptureFailure(_core.CloseAsync()));
            Assert.AreEqual(1, second.Shuts);
            // The observed failed close is retained. Reset should not re-log an already observed failure.
        });

        [UnityTest]
        public IEnumerator RetiredCoreCannotPublishLateInitialization() => UniTask.ToCoroutine(async () =>
        {
            var pending = new UniTaskCompletionSource<UniCoreException?>();
            Core previous = _core;
            var module = new Module("old") { Init = (_, __) => pending.Task };
            previous.RegisterModule(module);
            UniTask startup = previous.RunAsync();
            Core.ResetForPlay();
            _core = Core.Inst;
            await _core.RunAsync();
            pending.TrySetResult(null);
            Assert.IsInstanceOf<OperationCanceledException>(await CaptureFailure(startup));
            await previous.CloseAsync();
            Assert.True(_core.IsRunning);
            Assert.AreEqual(1, module.Shuts);
            Assert.AreEqual(0, module.Starts);
            Assert.Throws<ObjectDisposedException>(() => previous.RunAsync());
        });

        [Test]
        public void RegistrationFailureRollsBackAllNewMappings()
        {
            var broken = new Module("broken");
            broken.Register = core =>
            {
                core.RegisterInterface<IService, Module>(broken);
                throw new InvalidOperationException("register");
            };
            Assert.Throws<InvalidOperationException>(() => _core.RegisterModule(broken));
            Assert.Throws<InvalidOperationException>(() => _core.GetModule<IService>());
            var valid = new Module("valid");
            valid.Register = core => core.RegisterInterface<IService, Module>(valid);
            _core.RegisterModule(valid);
            Assert.AreSame(valid, _core.GetModule<IService>());
        }

        private static async UniTask<UniCoreException?> YieldInitialization(ICore core, CancellationToken token)
        {
            await UniTask.Yield();
            token.ThrowIfCancellationRequested();
            return null;
        }

        private static async UniTask<UniCoreException?> WaitForCancellation(ICore core, CancellationToken token)
        {
            var completion = new UniTaskCompletionSource<UniCoreException?>();
            using (token.Register(() => completion.TrySetCanceled(token))) { return await completion.Task; }
        }

        private static void ConfigureFailure(Module module, string phase)
        {
            switch (phase)
            {
                case "initialize-return": module.Init = (_, __) => UniTask.FromResult<UniCoreException?>(new UniCoreException("init")); break;
                case "initialize-throw": module.Init = (_, __) => throw new InvalidOperationException("init"); break;
                case "binding": module.Bind = () => throw new InvalidOperationException("bind"); break;
                case "start-return": module.StartAction = (_, __) => UniTask.FromResult<UniCoreException?>(new UniCoreException("start")); break;
                case "start-throw": module.StartAction = (_, __) => throw new InvalidOperationException("start"); break;
                default: module.After = () => throw new InvalidOperationException("after"); break;
            }
        }

        private static async UniTask<Exception?> CaptureFailure(UniTask task)
        {
            try { await task; return null; }
            catch (Exception error) { return error; }
        }

        private static async UniTask StartAfterClose(ICore core)
        {
            await core.CloseAsync();
            await core.RunAsync();
        }

        private interface IService { }

        [ModuleOrder(10)]
        private sealed class Module : IModule, IService
        {
            private readonly string _name;
            private readonly List<string>? _shutdownTrace;
            internal int Initializations, Starts, Shuts;
            internal Action<ICore>? Register;
            internal Func<ICore, CancellationToken, UniTask<UniCoreException?>>? Init, StartAction;
            internal Action? Bind, After, OnShut;

            internal Module(string name, List<string>? shutdownTrace = null) { _name = name; _shutdownTrace = shutdownTrace; }
            public string GetModuleName() => _name;
            public void OnRegister(ICore core) { Register?.Invoke(core); }
            public UniTask<UniCoreException?> Initialize(ICore core, CancellationToken token)
            {
                Initializations++;
                return Init?.Invoke(core, token) ?? UniTask.FromResult<UniCoreException?>(null);
            }
            public void AfterInitialize(ICore core) { Bind?.Invoke(); }
            public UniTask<UniCoreException?> Start(ICore core, CancellationToken token)
            {
                Starts++;
                return StartAction?.Invoke(core, token) ?? UniTask.FromResult<UniCoreException?>(null);
            }
            public void AfterStart(ICore core) { After?.Invoke(); }
            public void Shut(ICore core) { Shuts++; _shutdownTrace?.Add(_name); OnShut?.Invoke(); }
        }
    }
}
