using System.Collections;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.Assets.Editor;
using AiNative.UnityCore.Interfaces;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UIOwnershipTests
    {
        private const string Folder = "Assets/UIOwnershipFixture";
        private const string Address = Folder + "/Hud";
        private const string PrefabPath = Address + ".prefab";
        private AssetsModule _assets;
        private UIModule _ui;
        private GameObject _events;
        private AssetGroups _groups;
        private bool _bound;
        private int _enabled;
        private System.Action _onUnbind;
        private System.Action _onBind;
        private System.Action _onSuspend;
        private Core _core;
        private int _unbound;
        private int _suspended;
        private bool _expectCloseFailure;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            Core.ResetForPlay();
            _bound = false;
            _enabled = 0;
            _onUnbind = () => { };
            _onBind = () => { };
            _onSuspend = () => { };
            _unbound = 0;
            _suspended = 0;
            _expectCloseFailure = false;
            AssetDatabase.CreateFolder("Assets", "UIOwnershipFixture");
            var source = new GameObject("Hud", typeof(RectTransform), typeof(CanvasGroup));
            source.SetActive(false);
            var view = source.AddComponent<OwnershipView>();
            var safe = new GameObject("SafeArea", typeof(RectTransform));
            safe.transform.SetParent(source.transform, false);
            var visual = new GameObject("Visual", typeof(RectTransform));
            visual.transform.SetParent(safe.transform, false);
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_canvasGroup").objectReferenceValue = source.GetComponent<CanvasGroup>();
            serialized.FindProperty("_safeArea").objectReferenceValue = safe.transform;
            serialized.FindProperty("_visual").objectReferenceValue = visual.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
            Object.DestroyImmediate(source);
            _groups = ScriptableObject.CreateInstance<AssetGroups>();
            _groups.EditorLoadMode = EditorAssetLoadMode.AssetDatabase;
            _groups.Groups = new[] { new ResourceGroup { Id = "ui", Assets = new[] { new ResourceAddress { Asset = prefab } } } };
            _assets = EditorAssets.CreateConfigured(_groups);
            _events = new GameObject("UI Test EventSystem", typeof(EventSystem));
            _ui = new UIModule(new UIOptions(), _events.GetComponent<EventSystem>());
            _ui.Register(new UIWindowDefinition<OwnershipView>(Address, UIRole.Hud,
                (instance, scope) => new ProbePresenter(() => { _bound = true; _onBind(); },
                    () => { _unbound++; _onUnbind(); }, () => { _suspended++; _onSuspend(); })));
            OwnershipView.Enabled = () => { Assert.IsTrue(_bound, "Bind must precede OnEnable."); _enabled++; };
            _core = Core.Inst;
            _core.RegisterModule(_assets).RegisterModule(_ui);
            await _core.RunAsync();
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            OwnershipView.Enabled = null;
            _onSuspend = () => { };
            _onUnbind = () => { };
            System.Exception closeFailure = null;
            try { await _core.CloseAsync(); }
            catch (System.Exception error) { closeFailure = error; }
            Assert.AreEqual(_expectCloseFailure, closeFailure != null);
            Object.DestroyImmediate(_events);
            Object.DestroyImmediate(_groups);
            AssetDatabase.DeleteAsset(Folder);
            Core.ResetForPlay();
        });

        [UnityTest]
        public IEnumerator HudOpenOwnsOneScopeAndCloseReleaseReturnsToBaseline() => UniTask.ToCoroutine(async () =>
        {
            int scopes = _assets.GetDiagnostics().Scopes.Count;
            var opened = await _ui.OpenAsync<OwnershipView>();
            Assert.IsTrue(opened.Succeeded);
            var view = opened.View;
            Assert.Greater(_assets.GetDiagnostics().Resources.Count, 0);
            Assert.AreEqual(scopes + 1, _assets.GetDiagnostics().Scopes.Count);
            Assert.IsTrue((await _ui.CloseAsync(opened.Handle)).Succeeded);
            Assert.IsTrue(view == null, "Close with Release policy waits for actual destruction.");
            Assert.AreEqual(scopes, _assets.GetDiagnostics().Scopes.Count);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator InactivePrefabBindsBeforeEnable() => UniTask.ToCoroutine(async () =>
        {
            var opened = await _ui.OpenAsync<OwnershipView>();
            Assert.IsTrue(opened.Succeeded);
            Assert.IsTrue(_bound);
            Assert.AreEqual(1, _enabled);
            Assert.IsTrue(opened.View.gameObject.activeInHierarchy);
        });

        [UnityTest]
        public IEnumerator RepeatedCloseHasIndependentAwaiters() => UniTask.ToCoroutine(async () =>
        {
            var opened = await _ui.OpenAsync<OwnershipView>();
            var first = _ui.CloseAsync(opened.Handle);
            var second = _ui.CloseAsync(opened.Handle);
            Assert.IsTrue((await first).Succeeded);
            Assert.IsTrue((await second).Succeeded);
        });

        [UnityTest]
        public IEnumerator ForeignOwnerAndRestartInvalidateHandles() => UniTask.ToCoroutine(async () =>
        {
            var opened = await _ui.OpenAsync<OwnershipView>();
            var other = new UIModule(new UIOptions(), _events.GetComponent<EventSystem>());
            other.AfterInitialize(Core.Inst);
            await other.Start(Core.Inst, default);
            try
            {
                Assert.AreEqual(UIError.InvalidHandle, (await other.CloseAsync(opened.Handle)).Error);
            }
            finally
            {
                await other.ShutdownAsync(Core.Inst, default);
                other.Shut(Core.Inst);
            }
            await _core.RestartAsync();
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.CloseAsync(opened.Handle)).Error);
        });

        [UnityTest]
        public IEnumerator CoreCloseUnbindsBeforeAssetsShutdownAndDestroysWindows() => UniTask.ToCoroutine(async () =>
        {
            var opened = await _ui.OpenAsync<OwnershipView>();
            _onUnbind = () =>
            {
                Assert.IsTrue(opened.View != null);
                // Creating a scope proves Assets is still accepting work during UI teardown.
                _assets.CreateScope("unbind-order");
            };
            var first = _core.CloseAsync();
            var second = _core.CloseAsync();
            await first;
            await second;
            Assert.AreEqual(CoreState.Stopped, _core.State);
            Assert.AreEqual(1, _suspended);
            Assert.AreEqual(1, _unbound);
            Assert.IsTrue(opened.View == null);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator CoreRetirementSynchronouslyUnbindsVisibleWindows() => UniTask.ToCoroutine(async () =>
        {
            var opened = await _ui.OpenAsync<OwnershipView>();
            Core.RetireCurrent();
            Assert.AreEqual(1, _suspended);
            Assert.AreEqual(1, _unbound);
            Assert.IsFalse(_ui.TryGet<OwnershipView>(out _, out _));
            Assert.IsTrue(opened.View == null);
            await _core.CloseAsync();
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator CoreRestartCreatesNewWindowAndInvalidatesPreviousRun() => UniTask.ToCoroutine(async () =>
        {
            var old = await _ui.OpenAsync<OwnershipView>();
            await _core.RestartAsync();
            var current = await _ui.OpenAsync<OwnershipView>();
            Assert.IsTrue(old.View == null);
            Assert.IsTrue(current.Succeeded);
            Assert.AreNotEqual(old.Handle, current.Handle);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.CloseAsync(old.Handle)).Error);
            Assert.IsTrue(_ui.TryGet<OwnershipView>(out var view, out _));
            Assert.AreSame(current.View, view);
        });

        [UnityTest]
        public IEnumerator CancellationFromOnEnableRollsBackBeforeOpenSuccess() => UniTask.ToCoroutine(async () =>
        {
            using var cancellation = new System.Threading.CancellationTokenSource();
            OwnershipView.Enabled = cancellation.Cancel;
            System.Exception failure = null;
            try { await _ui.OpenAsync<OwnershipView>(cancellation.Token); }
            catch (System.Exception error) { failure = error; }
            Assert.IsInstanceOf<System.OperationCanceledException>(failure);
            Assert.AreEqual(1, _unbound);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator CoreCloseDuringBindCancelsOpenAndDrainsIt() => UniTask.ToCoroutine(async () =>
        {
            UniTask closing = default;
            _onBind = () => { closing = _core.CloseAsync(); };
            System.Exception failure = null;
            try { await _ui.OpenAsync<OwnershipView>(); }
            catch (System.Exception error) { failure = error; }
            await closing;
            Assert.IsInstanceOf<System.OperationCanceledException>(failure);
            Assert.AreEqual(CoreState.Stopped, _core.State);
            Assert.AreEqual(1, _unbound);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator CoreCloseFromOnEnableCancelsOpenAndDrainsIt() => UniTask.ToCoroutine(async () =>
        {
            UniTask closing = default;
            OwnershipView.Enabled = () => { closing = _core.CloseAsync(); };
            System.Exception failure = null;
            try { await _ui.OpenAsync<OwnershipView>(); }
            catch (System.Exception error) { failure = error; }
            await closing;
            Assert.IsInstanceOf<System.OperationCanceledException>(failure);
            Assert.AreEqual(1, _unbound);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator RetirementFailureStillUnbindsOtherWindowsAndReleasesAssets() => UniTask.ToCoroutine(async () =>
        {
            await _core.CloseAsync();
            bool otherUnbound = false;
            _ui.Register(new UIWindowDefinition<UIView>(Address, UIRole.Overlay,
                (view, scope) => new ProbePresenter(() => { }, () => { otherUnbound = true; })));
            await _core.RunAsync();
            var first = await _ui.OpenAsync<OwnershipView>();
            var second = await _ui.OpenAsync<UIView>();
            _onSuspend = () => { throw new System.InvalidOperationException("forced suspend failure"); };
            _expectCloseFailure = true;
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: forced suspend failure");
            Core.RetireCurrent();
            Assert.IsTrue(otherUnbound);
            Assert.AreEqual(1, _unbound);
            Assert.IsTrue(first.View == null);
            Assert.IsTrue(second.View == null);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator CoreRetirementDuringBindWaitsForCallbackBeforeUnbind() => UniTask.ToCoroutine(async () =>
        {
            bool binding = false;
            _onBind = () =>
            {
                binding = true;
                Core.RetireCurrent();
                Assert.Zero(_unbound, "Do not reenter a partially executing Bind callback.");
                binding = false;
            };
            _onUnbind = () => Assert.IsFalse(binding);
            System.Exception failure = null;
            try { await _ui.OpenAsync<OwnershipView>(); }
            catch (System.Exception error) { failure = error; }
            Assert.IsInstanceOf<System.OperationCanceledException>(failure);
            Assert.AreEqual(1, _unbound);
            Assert.Zero(_suspended, "Resume must not run after retirement from Bind.");
            await _core.CloseAsync();
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator CancellationAfterSuccessfulOpenDoesNotCloseWindow() => UniTask.ToCoroutine(async () =>
        {
            using var cancellation = new System.Threading.CancellationTokenSource();
            var opened = await _ui.OpenAsync<OwnershipView>(cancellation.Token);
            cancellation.Cancel();
            Assert.IsTrue(_ui.TryGet<OwnershipView>(out var view, out var handle));
            Assert.AreSame(opened.View, view);
            Assert.AreEqual(opened.Handle, handle);
            Assert.Zero(_unbound);
        });

        [UnityTest]
        public IEnumerator RetirementCleanupFailurePreservesOriginalBindFailure() => UniTask.ToCoroutine(async () =>
        {
            var bindError = new System.InvalidOperationException("original bind failure");
            var unbindError = new System.InvalidOperationException("retirement unbind failure");
            _onBind = () => { Core.RetireCurrent(); throw bindError; };
            _onUnbind = () => { if (_unbound == 1) { throw unbindError; } };
            System.Exception failure = null;
            try { await _ui.OpenAsync<OwnershipView>(); }
            catch (System.Exception error) { failure = error; }
            Assert.IsInstanceOf<System.AggregateException>(failure);
            var causes = ((System.AggregateException)failure).Flatten().InnerExceptions;
            CollectionAssert.Contains(causes, bindError);
            CollectionAssert.Contains(causes, unbindError);
            Assert.AreEqual(2, _unbound, "Rollback retries the incomplete Unbind.");
            await _core.CloseAsync();
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator SuspendFailureStillUnbindsAndRetryOnlyRepeatsFailedStep() => UniTask.ToCoroutine(async () =>
        {
            var opened = await _ui.OpenAsync<OwnershipView>();
            _onSuspend = () => { throw new System.InvalidOperationException("suspend failure"); };
            UIOperationException failure = null;
            try { await _ui.CloseAsync(opened.Handle); }
            catch (UIOperationException error) { failure = error; }
            Assert.IsNotNull(failure);
            Assert.AreEqual(1, _unbound, "Unbind is independent of Suspend failure.");
            Assert.IsTrue(opened.View == null);
            _onSuspend = () => { };
            Assert.IsTrue((await _ui.ReleaseAsync(failure.CleanupHandle.Value)).Succeeded);
            Assert.AreEqual(2, _suspended);
            Assert.AreEqual(1, _unbound);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator DuplicateTypeIsRejectedWhileOpening() => UniTask.ToCoroutine(async () =>
        {
            var first = _ui.OpenAsync<OwnershipView>();
            var second = await _ui.OpenAsync<OwnershipView>();
            Assert.AreEqual(UIError.AlreadyOpen, second.Error);
            Assert.IsTrue((await first).Succeeded);
        });

        [UnityTest]
        public IEnumerator RegisterWhileRunningIsRejected() => UniTask.ToCoroutine(async () =>
        {
            Assert.Throws<System.InvalidOperationException>(() => _ui.Register(
                new UIWindowDefinition<UIView>(Address, UIRole.Hud,
                    (view, assets) => new ProbePresenter(() => { }))));
            await UniTask.Yield();
        });

        [UnityTest]
        public IEnumerator StableVisibleWindowCanBeRetrievedUntilClose() => UniTask.ToCoroutine(async () =>
        {
            Assert.IsFalse(_ui.TryGet<OwnershipView>(out _, out _));
            var opened = await _ui.OpenAsync<OwnershipView>();
            Assert.IsTrue(_ui.TryGet<OwnershipView>(out var view, out var handle));
            Assert.AreSame(opened.View, view);
            Assert.AreEqual(opened.Handle, handle);
            await _ui.CloseAsync(handle);
            Assert.IsFalse(_ui.TryGet<OwnershipView>(out _, out _));
        });

        [UnityTest]
        public IEnumerator DiagnosticsTracksOwnedWindowUntilActualRelease() => UniTask.ToCoroutine(async () =>
        {
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
            var opened = await _ui.OpenAsync<OwnershipView>();
            var snapshot = _ui.GetDiagnostics();
            Assert.AreEqual(1, snapshot.WindowCount);
            Assert.Zero(snapshot.PendingOperations);
            await _ui.CloseAsync(opened.Handle);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
            Assert.AreEqual(1, snapshot.WindowCount, "Existing snapshots must not change after release.");
        });

        [UnityTest]
        public IEnumerator FailedRollbackRemainsInDiagnosticsUntilUnbindRetry() => UniTask.ToCoroutine(async () =>
        {
            _onBind = () => { throw new System.InvalidOperationException("bind failure"); };
            _onUnbind = () => { throw new System.InvalidOperationException("unbind failure"); };
            UIOperationException failure = null;
            try
            {
                await _ui.OpenAsync<OwnershipView>();
                Assert.Fail("Open must report the failed rollback.");
            }
            catch (UIOperationException error)
            {
                failure = error;
            }
            Assert.IsFalse(failure.Committed);
            Assert.IsTrue(failure.CleanupHandle.HasValue);
            Assert.IsFalse(failure.Handle.HasValue);
            Assert.AreEqual(1, _ui.GetDiagnostics().WindowCount);
            Assert.AreEqual(1, _ui.GetDiagnostics().FailedCleanupCount);
            Assert.Zero(_assets.GetDiagnostics().Resources.Count);
            _onUnbind = () => { };
            Assert.IsTrue((await _ui.ReleaseAsync(failure.CleanupHandle.Value)).Succeeded);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
            Assert.Zero(_ui.GetDiagnostics().FailedCleanupCount);
        });

        private sealed class ProbePresenter : UIPresenter
        {
            private readonly System.Action _bind;
            private readonly System.Action _unbind;
            private readonly System.Action _suspend;
            public ProbePresenter(System.Action bind) : this(bind, () => { }) { }
            public ProbePresenter(System.Action bind, System.Action unbind, System.Action suspend = null)
            {
                _bind = bind;
                _unbind = unbind;
                _suspend = suspend ?? (() => { });
            }
            public override void Bind() { _bind(); }
            public override void Resume() { }
            public override void Suspend() { _suspend(); }
            public override void Unbind() { _unbind(); }
        }
    }
}
