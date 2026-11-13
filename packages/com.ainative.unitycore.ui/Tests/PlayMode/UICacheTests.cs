using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.Assets.Editor;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UICacheTests
    {
        private const string Folder = "Assets/UICacheFixture";
        private Core _core;
        private UIModule _ui;
        private AssetsModule _assets;
        private AssetGroups _groups;
        private GameObject _eventObject;
        private readonly Dictionary<Type, Probe> _probes = new Dictionary<Type, Probe>();

        [SetUp]
        public void SetUp()
        {
            Core.ResetForPlay();
            _core = Core.Inst;
            _probes.Clear();
            NavigationView.Activation = null;
            NavigationSelectionProbe.SelectionChanged = null;
            AssetDatabase.CreateFolder("Assets", "UICacheFixture");
            var addresses = new[] { Prefab<FirstPageView>(), Prefab<SecondPageView>(), Prefab<ThirdPageView>(),
                Prefab<NavigationHudView>(), Prefab<FirstModalView>(), Prefab<SecondModalView>() };
            _groups = ScriptableObject.CreateInstance<AssetGroups>();
            _groups.EditorLoadMode = EditorAssetLoadMode.AssetDatabase;
            _groups.Groups = new[] { new ResourceGroup { Id = "cache", Assets = addresses } };
            _assets = EditorAssets.CreateConfigured(_groups);
            _eventObject = new GameObject("Cache EventSystem", typeof(EventSystem));
        }

        private async UniTask StartAsync(int capacity)
        {
            _ui = new UIModule(new UIOptions(closedCacheCapacity: capacity), _eventObject.GetComponent<EventSystem>());
            Register<FirstPageView>(UIRole.Page);
            Register<SecondPageView>(UIRole.Page);
            Register<ThirdPageView>(UIRole.Page);
            Register<NavigationHudView>(UIRole.Hud);
            Register<FirstModalView>(UIRole.Modal);
            Register<SecondModalView>(UIRole.Modal);
            _ui.Register(new UIWindowDefinition<UIView>(Folder + "/FirstPageView", UIRole.Overlay,
                (view, scope) => new Probe()));
            _core.RegisterModule(_assets).RegisterModule(_ui);
            await _core.RunAsync();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            NavigationView.Activation = null;
            foreach (var probe in _probes.Values) { probe.OnResume = probe.OnSuspend = probe.OnUnbind = () => { }; }
            await _core.CloseAsync();
            UnityEngine.Object.DestroyImmediate(_eventObject);
            UnityEngine.Object.DestroyImmediate(_groups);
            AssetDatabase.DeleteAsset(Folder);
            Core.ResetForPlay();
        });

        private ResourceAddress Prefab<T>() where T : UIView
        {
            var source = new GameObject(typeof(T).Name, typeof(RectTransform), typeof(CanvasGroup));
            source.SetActive(false);
            var view = source.AddComponent<T>();
            var safe = new GameObject("SafeArea", typeof(RectTransform));
            safe.transform.SetParent(source.transform, false);
            var visual = new GameObject("Visual", typeof(RectTransform));
            visual.transform.SetParent(safe.transform, false);
            var button = new GameObject("Default", typeof(RectTransform), typeof(Image), typeof(Button));
            button.transform.SetParent(visual.transform, false);
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_canvasGroup").objectReferenceValue = source.GetComponent<CanvasGroup>();
            serialized.FindProperty("_safeArea").objectReferenceValue = safe.transform;
            serialized.FindProperty("_visual").objectReferenceValue = visual.transform;
            serialized.FindProperty("_defaultSelection").objectReferenceValue = button.GetComponent<Button>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, Folder + "/" + typeof(T).Name + ".prefab");
            UnityEngine.Object.DestroyImmediate(source);
            return new ResourceAddress { Asset = prefab };
        }

        private void Register<T>(UIRole role) where T : UIView
        {
            var probe = new Probe();
            _probes.Add(typeof(T), probe);
            _ui.Register(new UIWindowDefinition<T>(Folder + "/" + typeof(T).Name, role,
                (view, scope) => { probe.Factories++; return probe; }, cachePolicy: UICachePolicy.Cache,
                opaqueFullscreen: role == UIRole.Page));
        }

        [UnityTest]
        public IEnumerator CachedReopenKeepsInstanceButChangesSession() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var first = await _ui.OpenAsync<NavigationHudView>();
            var parent = first.View.transform.parent;
            Assert.IsTrue((await _ui.CloseAsync(first.Handle)).Succeeded);
            Assert.IsTrue(first.View != null);
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.IsFalse(_ui.TryGet<NavigationHudView>(out _, out _));
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.Greater(_assets.GetDiagnostics().Resources.Count, 0);
            var next = await _ui.OpenAsync<NavigationHudView>();
            Assert.AreSame(first.View, next.View);
            Assert.AreSame(parent, next.View.transform.parent);
            Assert.AreNotEqual(first.Handle, next.Handle);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.CloseAsync(first.Handle)).Error);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.ReleaseAsync(first.Handle)).Error);
            Assert.AreEqual(UIError.InvalidHandle, _ui.BringToFront(first.Handle).Error);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            var probe = _probes[typeof(NavigationHudView)];
            Assert.AreEqual(1, probe.Factories);
            Assert.AreEqual(1, probe.Binds);
            Assert.AreEqual(2, probe.Resumes);
            Assert.AreEqual(1, probe.Suspends);
            Assert.Zero(probe.Unbinds);
        });

        [UnityTest]
        public IEnumerator LruCapacityOneEvictsOldestAfterRealDestruction() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var modal = await _ui.OpenAsync<FirstModalView>();
            var close = _ui.CloseAsync(modal.Handle);
            Assert.AreEqual(UniTaskStatus.Pending, close.Status);
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.AlreadyOpen, (await _ui.OpenAsync<NavigationHudView>()).Error);
            Assert.IsTrue((await close).Succeeded);
            Assert.IsTrue(hud.View == null);
            Assert.IsTrue(modal.View != null);
            Assert.IsFalse(modal.View.gameObject.activeSelf);
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.ReleaseAsync(hud.Handle)).Error);
        });

        [UnityTest]
        public IEnumerator ZeroCapacityReleases() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(0);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            Assert.IsTrue((await _ui.CloseAsync(hud.Handle)).Succeeded);
            Assert.IsTrue(hud.View == null);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator StackPagesDoNotCountAsClosedCache() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var first = await _ui.PushAsync<FirstPageView>();
            var second = await _ui.PushAsync<SecondPageView>();
            var third = await _ui.PushAsync<ThirdPageView>();
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            await _ui.BackAsync();
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            await _ui.BackAsync();
            Assert.IsTrue(third.View == null);
            Assert.IsTrue(second.View != null);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.AreEqual(1, _ui.GetDiagnostics().PageDepth);
            Assert.AreEqual(UIError.AlreadyOpen, (await _ui.PushAsync<FirstPageView>()).Error);
        });

        [UnityTest]
        public IEnumerator TrimDoesNotRemoveNavigationHistory() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var first = await _ui.PushAsync<FirstPageView>();
            var second = await _ui.PushAsync<SecondPageView>();
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var modal = await _ui.OpenAsync<FirstModalView>();
            Assert.IsTrue((await _ui.TrimAsync()).Succeeded);
            Assert.IsTrue(hud.View == null);
            Assert.IsTrue(first.View != null);
            Assert.IsTrue(second.View != null);
            Assert.IsTrue(modal.View != null);
            Assert.AreEqual(2, _ui.GetDiagnostics().PageDepth);
            Assert.IsTrue(_ui.HasModal);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
        });

        [UnityTest]
        public IEnumerator ReclosingAHitRefreshesLruButDuplicateCloseDoesNot() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var first = await _ui.OpenAsync<FirstModalView>();
            await _ui.CloseAsync(first.Handle);
            var hit = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hit.Handle);
            await _ui.CloseAsync(first.Handle);
            var second = await _ui.OpenAsync<SecondModalView>();
            await _ui.CloseAsync(second.Handle);
            Assert.IsTrue(first.View == null);
            Assert.IsTrue(hud.View != null);
            Assert.AreEqual(2, _ui.GetDiagnostics().ClosedCacheCount);
        });

        [UnityTest]
        public IEnumerator CancelledReopenRestoresOriginalHandleAndLruPosition() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var first = await _ui.OpenAsync<FirstModalView>();
            await _ui.CloseAsync(first.Handle);
            using var cancellation = new CancellationTokenSource();
            var probe = _probes[typeof(NavigationHudView)];
            probe.OnResume = cancellation.Cancel;
            try { await _ui.OpenAsync<NavigationHudView>(cancellation.Token); Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            probe.OnResume = () => { };
            Assert.AreEqual(2, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.IsTrue((await _ui.CloseAsync(hud.Handle)).Succeeded);
            Assert.IsFalse(hud.View.gameObject.activeSelf);
            Assert.AreEqual(1, probe.Binds);
            Assert.Zero(probe.Unbinds);
            var second = await _ui.OpenAsync<SecondModalView>();
            await _ui.CloseAsync(second.Handle);
            Assert.IsTrue(hud.View == null);
            Assert.IsTrue(first.View != null);
        });

        [UnityTest]
        public IEnumerator ClosedHandleCanReleaseUntilReopenedAndReleasePolicyNeverCaches() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            Assert.AreEqual(UIError.WindowBusy, (await _ui.ReleaseAsync(hud.Handle)).Error);
            await _ui.CloseAsync(hud.Handle);
            Assert.IsTrue((await _ui.ReleaseAsync(hud.Handle)).Succeeded);
            Assert.IsTrue(hud.View == null);
            var overlay = await _ui.OpenAsync<UIView>();
            await _ui.CloseAsync(overlay.Handle);
            Assert.IsTrue(overlay.View == null);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
        });

        [UnityTest]
        public IEnumerator TrimSnapshotSkipsReopenedAndReclosedSession() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var modal = await _ui.OpenAsync<FirstModalView>();
            await _ui.CloseAsync(modal.Handle);
            var trim = _ui.TrimAsync();
            Assert.AreEqual(UniTaskStatus.Pending, trim.Status);
            Assert.AreEqual(2, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.AlreadyOpen, (await _ui.OpenAsync<NavigationHudView>()).Error);
            var reopened = await _ui.OpenAsync<FirstModalView>();
            await _ui.CloseAsync(reopened.Handle);
            Assert.IsTrue((await trim).Succeeded);
            Assert.IsTrue(hud.View == null);
            Assert.IsTrue(modal.View != null);
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.ReleaseAsync(modal.Handle)).Error);
            Assert.IsTrue((await _ui.ReleaseAsync(reopened.Handle)).Succeeded);
        });

        [UnityTest]
        public IEnumerator FailedEvictionRetainsCapacityAndCleanupHandleUntilRetry() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var probe = _probes[typeof(NavigationHudView)];
            probe.OnUnbind = () => throw new InvalidOperationException("cache unbind failure");
            var modal = await _ui.OpenAsync<FirstModalView>();
            UIHandle retry = default;
            try { await _ui.CloseAsync(modal.Handle); Assert.Fail("Expected eviction failure."); }
            catch (UIOperationException error)
            {
                Assert.IsTrue(error.Committed);
                Assert.IsTrue(error.CleanupHandle.HasValue);
                retry = error.CleanupHandle.Value;
            }
            Assert.IsTrue(modal.View == null);
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(1, _ui.GetDiagnostics().FailedCleanupCount);
            Assert.AreEqual(UIError.AlreadyOpen, (await _ui.OpenAsync<NavigationHudView>()).Error);
            probe.OnUnbind = () => { };
            Assert.IsTrue((await _ui.ReleaseAsync(retry)).Succeeded);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator TrimContinuesAfterFailureAndCanRetry() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var modal = await _ui.OpenAsync<FirstModalView>();
            await _ui.CloseAsync(modal.Handle);
            var probe = _probes[typeof(NavigationHudView)];
            probe.OnUnbind = () => throw new InvalidOperationException("trim unbind failure");
            try { await _ui.TrimAsync(); Assert.Fail("Expected trim failure."); }
            catch (UIOperationException error) { Assert.IsTrue(error.Committed); Assert.IsTrue(error.CleanupHandle.HasValue); }
            Assert.IsTrue(modal.View == null);
            Assert.AreEqual(1, _ui.GetDiagnostics().ClosedCacheCount);
            probe.OnUnbind = () => { };
            Assert.IsTrue((await _ui.TrimAsync()).Succeeded);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator CachedPageReopenPreservesBindingAndBackHistory() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var first = await _ui.PushAsync<FirstPageView>();
            var second = await _ui.PushAsync<SecondPageView>();
            var parent = second.View.transform.parent;
            await _ui.BackAsync();
            var hit = await _ui.PushAsync<SecondPageView>();
            Assert.AreSame(second.View, hit.View);
            Assert.AreSame(parent, hit.View.transform.parent);
            Assert.AreEqual(1, _probes[typeof(SecondPageView)].Binds);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.CloseAsync(second.Handle)).Error);
            await _ui.BackAsync();
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.AreEqual(1, _ui.GetDiagnostics().PageDepth);
        });

        [UnityTest]
        public IEnumerator CoreCloseDrainsCacheAndRestartRejectsOldHandles() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            await _core.CloseAsync();
            Assert.IsTrue(hud.View == null);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
            Assert.AreEqual(1, _probes[typeof(NavigationHudView)].Unbinds);
            await _core.RunAsync();
            var next = await _ui.OpenAsync<NavigationHudView>();
            Assert.IsTrue(next.Succeeded);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.CloseAsync(hud.Handle)).Error);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.ReleaseAsync(hud.Handle)).Error);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
        });

        [UnityTest]
        public IEnumerator FailedColdSuspendStillUnbindsAndAllowsCleanupRetry() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            var probe = _probes[typeof(NavigationHudView)];
            probe.OnSuspend = () => throw new InvalidOperationException("cold suspend failure");
            UIHandle retry = default;
            try { await _ui.CloseAsync(hud.Handle); Assert.Fail("Expected suspend failure."); }
            catch (UIOperationException error)
            {
                Assert.IsTrue(error.Committed);
                Assert.IsTrue(error.CleanupHandle.HasValue);
                retry = error.CleanupHandle.Value;
            }
            Assert.IsTrue(hud.View == null);
            Assert.AreEqual(1, probe.Unbinds);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(1, _ui.GetDiagnostics().FailedCleanupCount);
            probe.OnSuspend = () => { };
            Assert.IsTrue((await _ui.ReleaseAsync(retry)).Succeeded);
            Assert.AreEqual(1, probe.Unbinds);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator CoreRestartFromCachedResumeCancelsOldSession() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var probe = _probes[typeof(NavigationHudView)];
            UniTask restart = default;
            probe.OnResume = () => { restart = _core.RestartAsync(); };
            try { await _ui.OpenAsync<NavigationHudView>(); Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            await restart;
            Assert.IsTrue(hud.View == null);
            Assert.AreEqual(1, probe.Unbinds);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            probe.OnResume = () => { };
            Assert.IsTrue((await _ui.OpenAsync<NavigationHudView>()).Succeeded);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.ReleaseAsync(hud.Handle)).Error);
        });

        [UnityTest]
        public IEnumerator CancelledReopenWithFailedSuspendReturnsRetryableCleanupHandle() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(1);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var probe = _probes[typeof(NavigationHudView)];
            using var cancellation = new CancellationTokenSource();
            probe.OnResume = cancellation.Cancel;
            probe.OnSuspend = () => throw new InvalidOperationException("rollback suspend failure");
            UIHandle retry = default;
            try { await _ui.OpenAsync<NavigationHudView>(cancellation.Token); Assert.Fail("Expected rollback failure."); }
            catch (UIOperationException error)
            {
                Assert.IsFalse(error.Committed);
                Assert.IsTrue(error.CleanupHandle.HasValue);
                retry = error.CleanupHandle.Value;
            }
            Assert.IsTrue(hud.View == null);
            Assert.Zero(_ui.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.ReleaseAsync(hud.Handle)).Error);
            probe.OnSuspend = probe.OnResume = () => { };
            Assert.IsTrue((await _ui.ReleaseAsync(retry)).Succeeded);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator ConcurrentTrimAndCoreCloseDrainEachCachedWindowOnce() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(2);
            var hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.CloseAsync(hud.Handle);
            var modal = await _ui.OpenAsync<FirstModalView>();
            await _ui.CloseAsync(modal.Handle);
            var first = _ui.TrimAsync();
            var second = _ui.TrimAsync();
            var close = _core.CloseAsync();
            Assert.IsTrue((await first).Succeeded);
            Assert.IsTrue((await second).Succeeded);
            await close;
            Assert.IsTrue(hud.View == null);
            Assert.IsTrue(modal.View == null);
            Assert.AreEqual(1, _probes[typeof(NavigationHudView)].Unbinds);
            Assert.AreEqual(1, _probes[typeof(FirstModalView)].Unbinds);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        private sealed class Probe : UIPresenter
        {
            internal int Factories, Binds, Resumes, Suspends, Unbinds;
            internal Action OnResume = () => { };
            internal Action OnSuspend = () => { };
            internal Action OnUnbind = () => { };
            public override void Bind() { Binds++; }
            public override void Resume() { Resumes++; OnResume(); }
            public override void Suspend() { Suspends++; OnSuspend(); }
            public override void Unbind() { Unbinds++; OnUnbind(); }
        }
    }
}
