#nullable enable
using System;
using System.Collections;
using System.Threading;
using AiNative.UnityCore.Exceptions;
using AiNative.UnityCore.Interfaces;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.Tests
{
    public sealed class CorePlayCycleTests
    {
        private const string ShutdownKey = "AiNative.UnityCore.Tests.ShutdownCount";
        private const string CancelKey = "AiNative.UnityCore.Tests.CancelCount";

        [UnityTest]
        public IEnumerator TwoPlaySessionsReleaseRunningModules()
        {
            SessionState.SetInt(ShutdownKey, 0);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Explicit boundaries avoid relying on coroutine loop locals surviving domain reload.
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            Assert.IsNull(Core.Inst.RootGameObject);
            Core.Inst.RegisterModule(new CycleModule(false));
            yield return Core.Inst.RunAsync().ToCoroutine();
            Assert.True(Core.Inst.IsRunning);
            yield return new ExitPlayMode();
            Assert.AreEqual(1, SessionState.GetInt(ShutdownKey, -1));
            Assert.IsNull(GameObject.Find("UnityCoreRoot"));

            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            Assert.IsNull(Core.Inst.RootGameObject);
            Assert.AreEqual(CoreState.Stopped, Core.Inst.State);
            Core.Inst.RegisterModule(new CycleModule(false));
            yield return Core.Inst.RunAsync().ToCoroutine();
            Assert.True(Core.Inst.IsRunning);
            yield return new ExitPlayMode();
            Assert.AreEqual(2, SessionState.GetInt(ShutdownKey, -1));
            Assert.IsNull(GameObject.Find("UnityCoreRoot"));
        }

        [UnityTest]
        public IEnumerator ExitingDuringInitializationCancelsBeforeTheNextPlay()
        {
            SessionState.SetInt(ShutdownKey, 0);
            SessionState.SetInt(CancelKey, 0);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            Core.Inst.RegisterModule(new CycleModule(true));
            Core.Inst.RegisterModule(new CycleModule(true));
            Core.Inst.RunAsync().Forget(error =>
            {
                if (!(error is OperationCanceledException)) { Debug.LogException(error); }
            });
            Assert.AreEqual(CoreState.Starting, Core.Inst.State);
            yield return new ExitPlayMode();
            Assert.AreEqual(2, SessionState.GetInt(CancelKey, -1));
            Assert.AreEqual(2, SessionState.GetInt(ShutdownKey, -1));
            yield return new EnterPlayMode(expectDomainReload: ReloadEnabled());
            Assert.AreEqual(CoreState.Stopped, Core.Inst.State);
            Core.Inst.RegisterModule(new CycleModule(false));
            yield return Core.Inst.RunAsync().ToCoroutine();
            yield return new ExitPlayMode();
            Assert.AreEqual(3, SessionState.GetInt(ShutdownKey, -1));
            Assert.AreEqual(2, SessionState.GetInt(CancelKey, -1));
        }

        private static bool ReloadEnabled()
        {
            return !EditorSettings.enterPlayModeOptionsEnabled ||
                !EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload);
        }

        private sealed class CycleModule : IModule
        {
            private readonly bool _pending;
            internal CycleModule(bool pending) { _pending = pending; }
            public string GetModuleName() => "cycle";
            public void OnRegister(ICore core) { }
            public async UniTask<UniCoreException?> Initialize(ICore core, CancellationToken token)
            {
                if (!_pending) { return null; }
                var gate = new UniTaskCompletionSource<UniCoreException?>();
                using (token.Register(() => gate.TrySetCanceled(token)))
                {
                    try { return await gate.Task; }
                    finally { SessionState.SetInt(CancelKey, SessionState.GetInt(CancelKey, 0) + 1); }
                }
            }
            public void AfterInitialize(ICore core) { }
            public UniTask<UniCoreException?> Start(ICore core, CancellationToken token) => UniTask.FromResult<UniCoreException?>(null);
            public void AfterStart(ICore core) { }
            public void Shut(ICore core)
            {
                SessionState.SetInt(ShutdownKey, SessionState.GetInt(ShutdownKey, 0) + 1);
            }
        }
    }
}
