using System;
using System.Collections;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UILateTaskTests : UITask5Fixture
    {
        private sealed class StartingGate : AiNative.UnityCore.Interfaces.IModule
        {
            internal readonly UniTaskCompletionSource Gate = new UniTaskCompletionSource();
            public string GetModuleName() => "UI startup gate";
            public void OnRegister(AiNative.UnityCore.Interfaces.ICore core) { }
            public UniTask<AiNative.UnityCore.Exceptions.UniCoreException> Initialize(AiNative.UnityCore.Interfaces.ICore core, System.Threading.CancellationToken token) => UniTask.FromResult<AiNative.UnityCore.Exceptions.UniCoreException>(null);
            public void AfterInitialize(AiNative.UnityCore.Interfaces.ICore core) { }
            public async UniTask<AiNative.UnityCore.Exceptions.UniCoreException> Start(AiNative.UnityCore.Interfaces.ICore core, System.Threading.CancellationToken token)
            { await Gate.Task; token.ThrowIfCancellationRequested(); return null; }
            public void AfterStart(AiNative.UnityCore.Interfaces.ICore core) { }
            public void Shut(AiNative.UnityCore.Interfaces.ICore core) { }
        }

        [UnityTest]
        public IEnumerator CloseDuringCoreStartupDrainsAlreadyStartedUi() => UniTask.ToCoroutine(async () =>
        {
            var gate = new StartingGate();
            var startup = StartAsync(afterUI: gate);
            var hud = await UI.OpenAsync<NavigationHudView>();
            Assert.IsTrue(hud.Succeeded);
            var closing = Core.CloseAsync();
            gate.Gate.TrySetResult();
            try { await startup; } catch (OperationCanceledException) { }
            await closing;
            Assert.IsTrue(hud.View == null);
            Assert.AreEqual(UIError.NotRunning, (await UI.OpenAsync<NavigationHudView>()).Error);
            Assert.Zero(Assets.GetDiagnostics().Scopes.Count);
        });

        private sealed class DelayedBackend : IAssetBackend
        {
            internal readonly UniTaskCompletionSource<UnityEngine.Object> Loaded = new UniTaskCompletionSource<UnityEngine.Object>();
            internal int Unloads;
            public UniTask<AssetBundle> LoadBundleAsync(BundleEntry entry) => UniTask.FromResult<AssetBundle>(null);
            public UniTask<UnityEngine.Object> LoadAsync(AssetBundle bundle, AssetEntry entry, Type type, string subAsset) => Loaded.Task;
            public void Unload(AssetBundle bundle) { Unloads++; }
            public UniTask<Scene> LoadSceneAsync(AssetEntry entry) => throw new NotSupportedException();
            public UniTask UnloadSceneAsync(Scene scene) => throw new NotSupportedException();
        }

        private static AssetCatalog Catalog() => new AssetCatalog { UnityVersion = Application.unityVersion,
            Bundles = new[] { new BundleEntry { Name = "controlled" } },
            Assets = new[] { new AssetEntry { Address = Folder + "/NavigationHudView", Bundle = "controlled", Path = "hud" } } };

        [UnityTest]
        public IEnumerator OldNativeLoadAfterRetirementCannotPublishIntoRestartedModule() => UniTask.ToCoroutine(async () =>
        {
            var oldBackend = new DelayedBackend();
            var newBackend = new DelayedBackend();
            int generation = 0;
            Assets = new AssetsModule(() => (Catalog(), generation++ == 0 ? oldBackend : newBackend));
            await StartAsync();
            var opening = UI.OpenAsync<NavigationHudView>();
            Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
            var oldClear = UI.ClearAsync();
            AiNative.UnityCore.Core.RetireCurrent();
            await Core.CloseAsync();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/NavigationHudView.prefab");
            newBackend.Loaded.TrySetResult(prefab);
            AiNative.UnityCore.Core.ResetForPlay();
            Core = AiNative.UnityCore.Core.Inst;
            Core.RegisterModule(Assets).RegisterModule(UI);
            await Core.RunAsync();
            var current = await UI.OpenAsync<NavigationHudView>();
            Assert.IsTrue(current.Succeeded);
            oldBackend.Loaded.TrySetResult(prefab);
            try { await opening; Assert.Fail("Expected cancelled old open."); } catch (OperationCanceledException) { }
            try { await oldClear; Assert.Fail("Old clear must not replace the new run."); } catch (UIOperationException) { }
            await UniTask.Yield(); await UniTask.Yield();
            Assert.IsTrue(UI.TryGet<NavigationHudView>(out var view, out var handle));
            Assert.AreSame(current.View, view);
            Assert.AreEqual(current.Handle, handle);
            Assert.AreEqual(1, UI.GetDiagnostics().WindowCount);
            Assert.AreEqual(1, Probes[typeof(NavigationHudView)].Binds);
            Assert.GreaterOrEqual(oldBackend.Unloads, 1);
        });

        [UnityTest]
        public IEnumerator ClearCancelsWaitingLoadAndLateCompletionCannotRestoreOldHandle() => UniTask.ToCoroutine(async () =>
        {
            var backend = new DelayedBackend();
            Assets = new AssetsModule(() => (Catalog(), backend));
            await StartAsync();
            var opening = UI.OpenAsync<NavigationHudView>();
            var clear = UI.ClearAsync();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/NavigationHudView.prefab");
            backend.Loaded.TrySetResult(prefab);
            try { await opening; Assert.Fail("Expected cancelled pending open."); } catch (OperationCanceledException) { }
            Assert.IsTrue((await clear).Succeeded);
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.Zero(Probes[typeof(NavigationHudView)].Binds);
            Assert.IsTrue((await UI.OpenAsync<NavigationHudView>()).Succeeded);
        });
    }
}
