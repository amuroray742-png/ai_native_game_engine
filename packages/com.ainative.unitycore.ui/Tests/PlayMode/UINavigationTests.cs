using System;
using System.Collections;
using System.Collections.Generic;
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
    public sealed class UINavigationTests
    {
        private const string Folder = "Assets/UINavigationFixture";
        private Core _core;
        private UIModule _ui;
        private AssetsModule _assets;
        private AssetGroups _groups;
        private GameObject _eventObject;
        private EventSystem _events;
        private readonly Dictionary<Type, Probe> _probes = new Dictionary<Type, Probe>();

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            Core.ResetForPlay();
            NavigationView.Activation = null;
            NavigationSelectionProbe.SelectionChanged = null;
            _probes.Clear();
            AssetDatabase.CreateFolder("Assets", "UINavigationFixture");
            var addresses = new List<ResourceAddress>();
            addresses.Add(CreatePrefab<FirstPageView>());
            addresses.Add(CreatePrefab<SecondPageView>());
            addresses.Add(CreatePrefab<ThirdPageView>());
            addresses.Add(CreatePrefab<FirstModalView>());
            addresses.Add(CreatePrefab<SecondModalView>());
            addresses.Add(CreatePrefab<NavigationHudView>());
            _groups = ScriptableObject.CreateInstance<AssetGroups>();
            _groups.EditorLoadMode = EditorAssetLoadMode.AssetDatabase;
            _groups.Groups = new[] { new ResourceGroup { Id = "navigation", Assets = addresses.ToArray() } };
            _assets = EditorAssets.CreateConfigured(_groups);
            _eventObject = new GameObject("Navigation EventSystem", typeof(EventSystem));
            _events = _eventObject.GetComponent<EventSystem>();
            _ui = new UIModule(new UIOptions(maxPageDepth: 3), _events);
            Register<FirstPageView>(UIRole.Page, true);
            Register<SecondPageView>(UIRole.Page, true);
            Register<ThirdPageView>(UIRole.Page, false);
            Register<FirstModalView>(UIRole.Modal);
            Register<SecondModalView>(UIRole.Modal);
            Register<NavigationHudView>(UIRole.Hud);
            _core = Core.Inst;
            _core.RegisterModule(_assets).RegisterModule(_ui);
            await _core.RunAsync();
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            NavigationView.Activation = null;
            NavigationSelectionProbe.SelectionChanged = null;
            foreach (var probe in _probes.Values) { probe.ResetCallbacks(); }
            await _core.CloseAsync();
            UnityEngine.Object.DestroyImmediate(_eventObject);
            UnityEngine.Object.DestroyImmediate(_groups);
            AssetDatabase.DeleteAsset(Folder);
            Core.ResetForPlay();
        });

        private ResourceAddress CreatePrefab<T>() where T : UIView
        {
            var source = new GameObject(typeof(T).Name, typeof(RectTransform), typeof(CanvasGroup));
            source.SetActive(false);
            var view = source.AddComponent<T>();
            var safe = new GameObject("SafeArea", typeof(RectTransform));
            safe.transform.SetParent(source.transform, false);
            var visual = new GameObject("Visual", typeof(RectTransform));
            visual.transform.SetParent(safe.transform, false);
            var button = new GameObject("Default", typeof(RectTransform), typeof(Image), typeof(Button), typeof(NavigationSelectionProbe));
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

        private void Register<T>(UIRole role, bool opaque = false) where T : UIView
        {
            var probe = new Probe();
            _probes.Add(typeof(T), probe);
            _ui.Register(new UIWindowDefinition<T>(Folder + "/" + typeof(T).Name, role,
                (view, assets) => { probe.Factory(); return probe; }, opaqueFullscreen: opaque));
        }

        [UnityTest]
        public IEnumerator BackClosesModalBeforePage() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            var second = await _ui.PushAsync<SecondPageView>();
            var modal = await _ui.OpenAsync<FirstModalView>();
            Assert.IsTrue(modal.Succeeded);
            Assert.IsTrue(_ui.HasModal);
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
            Assert.IsTrue(modal.View == null);
            Assert.AreEqual(2, _ui.GetDiagnostics().PageDepth);
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
            Assert.IsTrue(second.View == null);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.AreSame(first.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator RootBackAndNonTopCloseAreRejected() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            Assert.IsTrue(first.Succeeded);
            Assert.AreEqual(UIError.NoBackTarget, (await _ui.BackAsync()).Error);
            Assert.AreEqual(UIError.NoBackTarget, (await _ui.CloseAsync(first.Handle)).Error);
            await _ui.PushAsync<SecondPageView>();
            Assert.AreEqual(UIError.NotTopPage, (await _ui.CloseAsync(first.Handle)).Error);
            Assert.AreEqual(UIError.WindowBusy, (await _ui.ReleaseAsync(first.Handle)).Error);
            Assert.AreEqual(UIError.InvalidRole, _ui.BringToFront(first.Handle).Error);
        });

        [UnityTest]
        public IEnumerator TransparentChainStopsAtOpaquePage() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            var second = await _ui.PushAsync<SecondPageView>();
            var third = await _ui.PushAsync<ThirdPageView>();
            Assert.IsTrue(third.Succeeded);
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.gameObject.activeSelf);
            Assert.IsTrue(third.View.gameObject.activeSelf);
            Assert.IsFalse(second.View.CanvasGroup.interactable);
            Assert.IsTrue(third.View.CanvasGroup.interactable);
            Assert.IsTrue(_ui.TryGet<SecondPageView>(out _, out _));
            Assert.IsFalse(_ui.TryGet<FirstPageView>(out _, out _));
        });

        [UnityTest]
        public IEnumerator PushLimitPreservesHistory() => UniTask.ToCoroutine(async () =>
        {
            await _core.CloseAsync();
            _ui.Register(new UIWindowDefinition<UIView>(Folder + "/FirstPageView", UIRole.Page,
                (view, assets) => new Probe()));
            await _core.RunAsync();
            await _ui.PushAsync<FirstPageView>();
            await _ui.PushAsync<SecondPageView>();
            await _ui.PushAsync<ThirdPageView>();
            Assert.AreEqual(UIError.StackLimitReached, (await _ui.PushAsync<UIView>()).Error);
            Assert.AreEqual(3, _ui.GetDiagnostics().PageDepth);
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
        });

        [UnityTest]
        public IEnumerator CancelledPushRestoresPageAndFocus() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            int scopes = _assets.GetDiagnostics().Scopes.Count;
            using var cancellation = new System.Threading.CancellationTokenSource();
            _probes[typeof(SecondPageView)].OnResume = cancellation.Cancel;
            var error = await CaptureFailure(_ui.PushAsync<SecondPageView>(cancellation.Token));
            Assert.IsInstanceOf<OperationCanceledException>(error);
            Assert.AreEqual(1, _ui.GetDiagnostics().PageDepth);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
            Assert.AreSame(first.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            Assert.AreEqual(scopes, _assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator BindOrResumeFailureRollsBackCandidate() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            var candidate = _probes[typeof(SecondPageView)];
            candidate.OnBind = () => throw new InvalidOperationException("bind failure");
            Assert.IsInstanceOf<InvalidOperationException>(await CaptureFailure(_ui.PushAsync<SecondPageView>()));
            candidate.ResetCallbacks();
            candidate.OnResume = () => throw new InvalidOperationException("resume failure");
            Assert.IsInstanceOf<InvalidOperationException>(await CaptureFailure(_ui.PushAsync<SecondPageView>()));
            Assert.AreEqual(2, candidate.Unbinds);
            Assert.AreEqual(1, _ui.GetDiagnostics().WindowCount);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.AreSame(first.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator FailedSuspendDuringPushResumesOriginalPage() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            var previous = _probes[typeof(FirstPageView)];
            previous.OnSuspend = () => throw new InvalidOperationException("partial suspend");
            Assert.IsInstanceOf<InvalidOperationException>(await CaptureFailure(_ui.PushAsync<SecondPageView>()));
            Assert.AreEqual(2, previous.Resumes);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
            Assert.AreEqual(1, _ui.GetDiagnostics().PageDepth);
        });

        [UnityTest]
        public IEnumerator FailedPreviousPageResumeLeavesCurrentPageAndAllowsRetry() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            var second = await _ui.PushAsync<SecondPageView>();
            _probes[typeof(FirstPageView)].OnResume = () => throw new InvalidOperationException("return failure");
            var error = await CaptureFailure(_ui.BackAsync()) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsFalse(error.Committed);
            Assert.AreEqual(second.Handle, error.Handle.Value);
            Assert.AreEqual(2, _ui.GetDiagnostics().PageDepth);
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.gameObject.activeSelf);
            Assert.AreSame(second.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            _probes[typeof(FirstPageView)].ResetCallbacks();
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
        });

        [UnityTest]
        public IEnumerator RollbackFailureReturnsCleanupHandle() => UniTask.ToCoroutine(async () =>
        {
            await _ui.PushAsync<FirstPageView>();
            var candidate = _probes[typeof(SecondPageView)];
            candidate.OnBind = () => throw new InvalidOperationException("bind failure");
            candidate.OnUnbind = () => throw new InvalidOperationException("unbind failure");
            var error = await CaptureFailure(_ui.PushAsync<SecondPageView>()) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsFalse(error.Committed);
            Assert.IsTrue(error.CleanupHandle.HasValue);
            Assert.IsFalse(error.Handle.HasValue);
            Assert.AreEqual(UIError.InvalidHandle, (await _ui.CloseAsync(error.CleanupHandle.Value)).Error);
            Assert.AreEqual(UIError.AlreadyOpen, (await _ui.PushAsync<SecondPageView>()).Error);
            candidate.ResetCallbacks();
            Assert.IsTrue((await _ui.ReleaseAsync(error.CleanupHandle.Value)).Succeeded);
            Assert.Zero(_ui.GetDiagnostics().FailedCleanupCount);
            Assert.AreEqual(1, _ui.GetDiagnostics().PageDepth);
        });

        [UnityTest]
        public IEnumerator UnbindRetryOutlivesReleasedModalScope() => UniTask.ToCoroutine(async () =>
        {
            await _ui.PushAsync<FirstPageView>();
            var modal = await _ui.OpenAsync<FirstModalView>();
            _probes[typeof(FirstModalView)].OnUnbind = () => throw new InvalidOperationException("unbind failure");
            var error = await CaptureFailure(_ui.CloseAsync(modal.Handle)) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsTrue(error.Committed);
            Assert.IsTrue(modal.View == null);
            Assert.IsFalse(error.Handle.HasValue);
            Assert.IsTrue(error.CleanupHandle.HasValue);
            Assert.IsFalse(_ui.HasModal);
            _probes[typeof(FirstModalView)].ResetCallbacks();
            Assert.IsTrue((await _ui.ReleaseAsync(error.CleanupHandle.Value)).Succeeded);
            Assert.AreEqual(1, _ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator PostCommitNotificationFailureRetainsCommittedIdentity() => UniTask.ToCoroutine(async () =>
        {
            int notified = 0;
            Action<bool> throwing = hasModal => throw new InvalidOperationException("notification failure");
            _ui.ModalStateChanged += throwing;
            _ui.ModalStateChanged += hasModal => { notified++; AssertReentrantRejected(); };
            var error = await CaptureFailure(_ui.OpenAsync<FirstModalView>()) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsTrue(error.Committed);
            Assert.IsTrue(error.Handle.HasValue);
            Assert.IsFalse(error.CleanupHandle.HasValue);
            Assert.IsTrue(_ui.TryGet<FirstModalView>(out _, out var handle));
            Assert.AreEqual(handle, error.Handle.Value);
            Assert.AreEqual(1, notified);
            _ui.ModalStateChanged -= throwing;
            Assert.IsTrue((await _ui.CloseAsync(handle)).Succeeded);
            Assert.AreEqual(2, notified);
        });

        [UnityTest]
        public IEnumerator ModalOrderMaskAndInputFollowBringToFront() => UniTask.ToCoroutine(async () =>
        {
            var hud = await _ui.OpenAsync<NavigationHudView>();
            var page = await _ui.PushAsync<FirstPageView>();
            var states = new List<bool>();
            _ui.ModalStateChanged += states.Add;
            var first = await _ui.OpenAsync<FirstModalView>();
            var second = await _ui.OpenAsync<SecondModalView>();
            Assert.AreEqual(2, _ui.GetDiagnostics().ModalCount);
            Assert.IsFalse(hud.View.CanvasGroup.interactable);
            Assert.IsFalse(page.View.CanvasGroup.interactable);
            Assert.IsFalse(first.View.CanvasGroup.interactable);
            Assert.IsTrue(second.View.CanvasGroup.interactable);
            var mask = second.View.transform.parent.Find("ModalMask");
            Assert.AreEqual(second.View.transform.GetSiblingIndex() - 1, mask.GetSiblingIndex());
            Assert.IsTrue(_ui.BringToFront(first.Handle).Succeeded);
            Assert.AreSame(first.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            Assert.AreEqual(first.View.transform.GetSiblingIndex() - 1, mask.GetSiblingIndex());
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
            Assert.IsTrue(first.View == null);
            Assert.IsTrue(second.View.CanvasGroup.interactable);
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
            Assert.IsTrue(page.View.CanvasGroup.interactable);
            Assert.IsFalse(mask.gameObject.activeSelf);
            CollectionAssert.AreEqual(new[] { true, false }, states);
        });

        [UnityTest]
        public IEnumerator NavigationBusyDoesNotBlockHudAndDuplicateCloseSharesCompletion() => UniTask.ToCoroutine(async () =>
        {
            var budget = _assets.CreateScope("consume-instance-budget");
            await budget.InstantiateAsync(Folder + "/FirstPageView");
            await budget.InstantiateAsync(Folder + "/SecondPageView");
            var pending = _ui.PushAsync<FirstPageView>();
            Assert.AreEqual(UIError.NavigationBusy, (await _ui.PushAsync<SecondPageView>()).Error);
            Assert.IsTrue((await _ui.OpenAsync<NavigationHudView>()).Succeeded);
            Assert.IsTrue((await pending).Succeeded);
            await budget.CloseAsync();
            var second = await _ui.PushAsync<SecondPageView>();
            var firstClose = _ui.CloseAsync(second.Handle);
            var secondClose = _ui.CloseAsync(second.Handle);
            Assert.AreEqual(UIError.NavigationBusy, (await _ui.BackAsync()).Error);
            Assert.IsTrue((await firstClose).Succeeded);
            Assert.IsTrue((await secondClose).Succeeded);
            Assert.IsTrue((await _ui.PushAsync<ThirdPageView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator AllExternalCallbacksRejectLifecycleReentry() => UniTask.ToCoroutine(async () =>
        {
            int checkedCallbacks = 0;
            Action check = () => { checkedCallbacks++; AssertReentrantRejected(); };
            foreach (var probe in _probes.Values)
            {
                probe.Factory = probe.OnBind = probe.OnResume = probe.OnSuspend = probe.OnUnbind = check;
            }
            NavigationView.Activation = (view, active) => check();
            NavigationSelectionProbe.SelectionChanged = check;
            await _ui.PushAsync<FirstPageView>();
            await _ui.PushAsync<SecondPageView>();
            await _ui.OpenAsync<FirstModalView>();
            await _ui.BackAsync();
            await _ui.BackAsync();
            Assert.Greater(checkedCallbacks, 15);
        });

        private void AssertReentrantRejected()
        {
            Assert.AreEqual(UIError.ReentrantCall, _ui.OpenAsync<FirstModalView>().GetAwaiter().GetResult().Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.PushAsync<FirstPageView>().GetAwaiter().GetResult().Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.BackAsync().GetAwaiter().GetResult().Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.CloseAsync(default).GetAwaiter().GetResult().Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.ReleaseAsync(default).GetAwaiter().GetResult().Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.BringToFront(default).Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.TrimAsync().GetAwaiter().GetResult().Error);
            Assert.AreEqual(UIError.ReentrantCall, _ui.ClearAsync().GetAwaiter().GetResult().Error);
        }

        [UnityTest]
        public IEnumerator ClosingModalRestoresHudFocusWhenHudRemainsInteractive() => UniTask.ToCoroutine(async () =>
        {
            var hud = await _ui.OpenAsync<NavigationHudView>();
            _events.SetSelectedGameObject(hud.View.DefaultSelection.gameObject);
            await _ui.OpenAsync<FirstModalView>();
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
            Assert.AreSame(hud.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            await _core.CloseAsync();
            _ui.Register(new UIWindowDefinition<UIView>(Folder + "/FirstPageView", UIRole.Page,
                (view, scope) => new Probe(), allowHudInput: true));
            await _core.RunAsync();
            hud = await _ui.OpenAsync<NavigationHudView>();
            await _ui.PushAsync<UIView>();
            Assert.IsTrue(hud.View.CanvasGroup.interactable);
            _events.SetSelectedGameObject(hud.View.DefaultSelection.gameObject);
            await _ui.OpenAsync<FirstModalView>();
            Assert.IsTrue((await _ui.BackAsync()).Succeeded);
            Assert.AreSame(hud.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator InvalidOldFocusFallsBackToActiveWindowDefault() => UniTask.ToCoroutine(async () =>
        {
            var page = await _ui.PushAsync<FirstPageView>();
            var extra = new GameObject("Old focus", typeof(RectTransform), typeof(Button));
            extra.transform.SetParent(page.View.Visual, false);
            _events.SetSelectedGameObject(extra);
            await _ui.OpenAsync<FirstModalView>();
            extra.GetComponent<Button>().interactable = false;
            await _ui.BackAsync();
            Assert.AreSame(page.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator FailedRollbackIsolatesHistoryAndPublishesModalRemoval() => UniTask.ToCoroutine(async () =>
        {
            var page = await _ui.PushAsync<FirstPageView>();
            var states = new List<bool>();
            _ui.ModalStateChanged += states.Add;
            var modal = await _ui.OpenAsync<FirstModalView>();
            var previous = _probes[typeof(FirstPageView)];
            previous.OnSuspend = () => { if (previous.Suspends == 1) { throw new InvalidOperationException("hide failure"); } };
            previous.OnResume = () => throw new InvalidOperationException("restore failure");
            await UniTask.NextFrame();
            var pending = _ui.PushAsync<SecondPageView>();
            Assert.IsFalse(modal.View.gameObject.activeSelf, "All isolated windows must hide before awaiting the first destruction.");
            Assert.IsFalse(_ui.TryGet<FirstModalView>(out _, out _));
            var error = await CaptureFailure(pending) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsFalse(error.Committed);
            Assert.IsTrue(page.View == null);
            Assert.IsTrue(modal.View == null);
            Assert.Zero(_ui.GetDiagnostics().PageDepth);
            Assert.Zero(_ui.GetDiagnostics().ModalCount);
            Assert.Zero(_ui.GetDiagnostics().WindowCount);
            CollectionAssert.AreEqual(new[] { true, false }, states);
            Assert.IsTrue((await _ui.PushAsync<ThirdPageView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator NotificationShutdownStopsLaterSubscribersAndPreservesAllCauses() => UniTask.ToCoroutine(async () =>
        {
            var original = new InvalidOperationException("notification failed before shutdown");
            UniTask closing = default;
            bool lateNotified = false;
            _ui.ModalStateChanged += value => throw original;
            _ui.ModalStateChanged += value => { closing = _core.CloseAsync(); };
            _ui.ModalStateChanged += value => { lateNotified = true; };
            var error = await CaptureFailure(_ui.OpenAsync<FirstModalView>()) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsTrue(error.Committed);
            var causes = ((AggregateException)error.InnerException).Flatten().InnerExceptions;
            CollectionAssert.Contains(causes, original);
            Assert.IsTrue(System.Linq.Enumerable.Any(causes, cause => cause is OperationCanceledException));
            Assert.IsFalse(lateNotified);
            await closing;
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
            await _core.RunAsync();
            Assert.IsTrue((await _ui.OpenAsync<FirstModalView>()).Succeeded);
            Assert.IsFalse(lateNotified, "Shut clears previous run subscriptions.");
        });

        [UnityTest]
        public IEnumerator ModalReorderingAndNonTopClosePreservePreModalHudFocus() => UniTask.ToCoroutine(async () =>
        {
            var hud = await _ui.OpenAsync<NavigationHudView>();
            _events.SetSelectedGameObject(hud.View.DefaultSelection.gameObject);
            NavigationView.Activation = (view, active) =>
            {
                if (active && view is FirstModalView) { _events.SetSelectedGameObject(view.DefaultSelection.gameObject); }
            };
            var first = await _ui.OpenAsync<FirstModalView>();
            Assert.IsTrue(_ui.BringToFront(first.Handle).Succeeded);
            await _ui.BackAsync();
            Assert.AreSame(hud.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            first = await _ui.OpenAsync<FirstModalView>();
            var second = await _ui.OpenAsync<SecondModalView>();
            await _ui.CloseAsync(first.Handle);
            Assert.AreEqual(1, _ui.GetDiagnostics().ModalCount);
            await _ui.CloseAsync(second.Handle);
            Assert.AreSame(hud.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            first = await _ui.OpenAsync<FirstModalView>();
            await _ui.OpenAsync<SecondModalView>();
            _ui.BringToFront(first.Handle);
            await _ui.BackAsync();
            await _ui.BackAsync();
            Assert.AreSame(hud.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator CoreCloseDuringModalReorderDoesNotRestoreRetiredHistory() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.OpenAsync<FirstModalView>();
            await _ui.OpenAsync<SecondModalView>();
            UniTask closing = default;
            NavigationSelectionProbe.SelectionChanged = () =>
            {
                NavigationSelectionProbe.SelectionChanged = null;
                closing = _core.CloseAsync();
            };
            var error = Assert.Throws<UIOperationException>(() => _ui.BringToFront(first.Handle));
            Assert.IsFalse(error.Committed);
            Assert.IsFalse(error.Handle.HasValue);
            await closing;
            Assert.Zero(_ui.GetDiagnostics().ModalCount);
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator CancellationAfterPageHideRestoresVisibilityAndFocus() => UniTask.ToCoroutine(async () =>
        {
            var first = await _ui.PushAsync<FirstPageView>();
            using var cancellation = new System.Threading.CancellationTokenSource();
            NavigationSelectionProbe.SelectionChanged = cancellation.Cancel;
            var error = await CaptureFailure(_ui.PushAsync<SecondPageView>(cancellation.Token));
            Assert.IsInstanceOf<OperationCanceledException>(error);
            Assert.AreEqual(2, _probes[typeof(FirstPageView)].Resumes);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
            Assert.AreSame(first.View.DefaultSelection.gameObject, _events.currentSelectedGameObject);
            Assert.AreEqual(1, _ui.GetDiagnostics().PageDepth);
            Assert.AreEqual(1, _ui.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator CoreCloseDuringCommittedModalReleaseReportsNotificationCancellation() => UniTask.ToCoroutine(async () =>
        {
            var states = new List<bool>();
            _ui.ModalStateChanged += states.Add;
            var modal = await _ui.OpenAsync<FirstModalView>();
            var closingWindow = _ui.CloseAsync(modal.Handle);
            Assert.IsFalse(_ui.HasModal, "Navigation is already committed while destruction drains.");
            var closingCore = _core.CloseAsync();
            var error = await CaptureFailure(closingWindow) as UIOperationException;
            Assert.IsNotNull(error);
            Assert.IsTrue(error.Committed);
            Assert.IsFalse(error.Handle.HasValue);
            Assert.IsFalse(error.CleanupHandle.HasValue);
            Assert.IsTrue(System.Linq.Enumerable.Any(((AggregateException)error.InnerException).Flatten().InnerExceptions,
                cause => cause is OperationCanceledException));
            await closingCore;
            Assert.IsTrue(modal.View == null);
            CollectionAssert.AreEqual(new[] { true }, states, "Retired runs must not publish delayed modal state.");
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator CoreCloseHidesAllWindowsBeforeAwaitingResourceDrain() => UniTask.ToCoroutine(async () =>
        {
            var page = await _ui.PushAsync<FirstPageView>();
            var modal = await _ui.OpenAsync<FirstModalView>();
            var closing = _core.CloseAsync();
            Assert.IsTrue(page.View == null || !page.View.gameObject.activeInHierarchy);
            Assert.IsTrue(modal.View == null || !modal.View.gameObject.activeInHierarchy);
            Assert.IsFalse(_ui.TryGet<FirstModalView>(out _, out _));
            await closing;
            Assert.Zero(_assets.GetDiagnostics().Scopes.Count);
        });

        private static async UniTask<Exception> CaptureFailure<T>(UniTask<T> operation)
        {
            try { await operation; }
            catch (Exception error) { return error; }
            Assert.Fail("Expected the operation to fail.");
            return null;
        }

        private sealed class Probe : UIPresenter
        {
            internal Action Factory = () => { };
            internal Action OnBind = () => { };
            internal Action OnResume = () => { };
            internal Action OnSuspend = () => { };
            internal Action OnUnbind = () => { };
            internal int Binds;
            internal int Resumes;
            internal int Suspends;
            internal int Unbinds;
            public override void Bind() { Binds++; OnBind(); }
            public override void Resume() { Resumes++; OnResume(); }
            public override void Suspend() { Suspends++; OnSuspend(); }
            public override void Unbind() { Unbinds++; OnUnbind(); }
            internal void ResetCallbacks()
            {
                Factory = OnBind = OnResume = OnSuspend = OnUnbind = () => { };
            }
        }
    }
}
