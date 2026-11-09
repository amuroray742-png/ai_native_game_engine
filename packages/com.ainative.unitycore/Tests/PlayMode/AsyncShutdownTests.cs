#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Tests
{
    public sealed class AsyncShutdownTests
    {
        private Core _core = null!;
        [SetUp] public void SetUp() { Core.ResetForPlay(); _core = Core.Inst; }
        [UnityTearDown] public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            try { await _core.CloseAsync(); } catch { }
            Core.ResetForPlay();
            await UniTask.Yield();
        });
        [UnityTest]
        public IEnumerator CloseWaitsForDrainBeforeShutAndPreservesReverseOrder() => UniTask.ToCoroutine(async () =>
        {
            var trace = new List<string>();
            var first = new Module("first", trace);
            var second = new Module("second", trace) { Gate = new UniTaskCompletionSource() };
            _core.RegisterModule(first).RegisterModule(second);
            await _core.RunAsync();
            var closing = _core.CloseAsync();
            Assert.AreEqual(CoreState.Closing, _core.State);
            CollectionAssert.AreEqual(new[] { "drain-second" }, trace);
            Assert.False(second.Forced.IsCancellationRequested);
            second.Gate.TrySetResult();
            await closing;
            CollectionAssert.AreEqual(new[] { "drain-second", "shut-second", "drain-first", "shut-first" }, trace);
            await _core.CloseAsync();
            Assert.AreEqual(4, trace.Count);
        });
        [UnityTest]
        public IEnumerator AsyncShutdownFailureStillShutsAllModulesAndPreventsRestart() => UniTask.ToCoroutine(async () =>
        {
            var trace = new List<string>();
            var first = new Module("first", trace);
            var second = new Module("second", trace) { FailShutdown = true };
            _core.RegisterModule(first).RegisterModule(second);
            await _core.RunAsync();
            Exception? failure = null;
            try { await _core.CloseAsync(); } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<InvalidOperationException>(failure);
            Assert.AreEqual(CoreState.Faulted, _core.State);
            CollectionAssert.AreEqual(new[] { "drain-second", "shut-second", "drain-first", "shut-first" }, trace);
            failure = null;
            try { await _core.RunAsync(); } catch (Exception error) { failure = error; }
            Assert.IsNotNull(failure);
        });
        [UnityTest]
        public IEnumerator RetirementCancelsDrainAndStillShutsEachModuleOnce() => UniTask.ToCoroutine(async () =>
        {
            var trace = new List<string>();
            var module = new Module("late", trace) { Gate = new UniTaskCompletionSource() };
            _core.RegisterModule(module);
            await _core.RunAsync();
            var closing = _core.CloseAsync();
            Core.RetireCurrent();
            await closing;
            Assert.True(module.Forced.IsCancellationRequested);
            CollectionAssert.AreEqual(new[] { "drain-late", "shut-late" }, trace);
            module.Gate.TrySetResult();
            await UniTask.Yield();
            Assert.AreEqual(2, trace.Count);
        });
        private sealed class Module : IModule, IAsyncShutdownModule
        {
            private readonly string _name;
            private readonly List<string> _trace;
            internal UniTaskCompletionSource? Gate;
            internal CancellationToken Forced;
            internal bool FailShutdown;
            internal Module(string name, List<string> trace) { _name = name; _trace = trace; }
            public string GetModuleName() => _name;
            public void OnRegister(ICore core) { }
            public UniTask<UniCoreException?> Initialize(ICore core, CancellationToken token) => UniTask.FromResult<UniCoreException?>(null);
            public void AfterInitialize(ICore core) { }
            public UniTask<UniCoreException?> Start(ICore core, CancellationToken token) => UniTask.FromResult<UniCoreException?>(null);
            public void AfterStart(ICore core) { }
            public UniTask ShutdownAsync(ICore core, CancellationToken forcedExit)
            {
                Forced = forcedExit;
                _trace.Add("drain-" + _name);
                if (FailShutdown) { throw new InvalidOperationException("Shutdown failed"); }
                return Gate?.Task ?? UniTask.CompletedTask;
            }
            public void Shut(ICore core) { _trace.Add("shut-" + _name); }
        }
    }
}
