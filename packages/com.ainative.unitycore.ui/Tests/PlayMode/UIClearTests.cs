using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UIClearTests : UITask5Fixture
    {
        [UnityTest]
        public IEnumerator ClearDrainsStackCacheAndPendingOpen() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(new UITransitionOptions(30, true, Vector2.zero, 0.8f), animateHud: false);
            var page = await UI.PushAsync<FirstPageView>();
            var hud = await UI.OpenAsync<NavigationHudView>();
            await UI.CloseAsync(hud.Handle);
            var opening = UI.OpenAsync<FirstModalView>();
            var clear = UI.ClearAsync();
            Assert.AreEqual(UIError.NotRunning, (await UI.OpenAsync<SecondModalView>()).Error);
            try { await opening; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert.IsTrue((await clear).Succeeded);
            Assert.IsTrue(page.View == null);
            Assert.IsTrue(hud.View == null);
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.Zero(Assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator ClearInvalidatesOldHandlesAndAcceptsNewRoot() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var old = await UI.PushAsync<FirstPageView>();
            Assert.IsTrue((await UI.ClearAsync()).Succeeded);
            var current = await UI.PushAsync<FirstPageView>();
            Assert.IsTrue(current.Succeeded);
            Assert.AreNotEqual(old.Handle, current.Handle);
            Assert.AreEqual(UIError.InvalidHandle, (await UI.CloseAsync(old.Handle)).Error);
            Assert.IsTrue((await UI.ClearAsync()).Succeeded);
        });

        [UnityTest]
        public IEnumerator DestroyedTopPageRestoresPreviousPage() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var first = await UI.PushAsync<FirstPageView>();
            var second = await UI.PushAsync<SecondPageView>();
            UnityEngine.Object.DestroyImmediate(second.View.gameObject);
            await UniTask.Yield();
            await UniTask.Yield();
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
            Assert.AreSame(first.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator FailedClearKeepsModelAndAllowsRetry() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            var hud = await UI.OpenAsync<NavigationHudView>();
            bool modelReleased = false;
            Probes[typeof(FirstPageView)].OnUnbind = () => throw new InvalidOperationException("model still subscribed");
            try { await UI.ClearAsync(); modelReleased = true; Assert.Fail("Expected failed clear."); }
            catch (UIOperationException) { }
            Assert.IsFalse(modelReleased);
            Assert.IsTrue(page.View == null);
            Assert.IsTrue(hud.View == null);
            Assert.AreEqual(1, Probes[typeof(NavigationHudView)].Unbinds);
            Assert.AreEqual(1, UI.GetDiagnostics().FailedCleanupCount);
            Assert.AreEqual(UIError.NotRunning, (await UI.PushAsync<FirstPageView>()).Error);
            Probes[typeof(FirstPageView)].OnUnbind = () => { };
            Assert.IsTrue((await UI.ClearAsync()).Succeeded);
            modelReleased = true;
            Assert.IsTrue(modelReleased);
            Assert.IsTrue((await UI.PushAsync<FirstPageView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator DuplicateClearSharesCompletionAndCoreClosePreventsReopening() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            await UI.PushAsync<FirstPageView>();
            var first = UI.ClearAsync();
            var second = UI.ClearAsync();
            var shutdown = Core.CloseAsync();
            try { await first; Assert.Fail("Expected interrupted clear."); } catch (UIOperationException) { }
            try { await second; Assert.Fail("Expected interrupted clear."); } catch (UIOperationException) { }
            await shutdown;
            Assert.AreEqual(UIError.NotRunning, (await UI.PushAsync<FirstPageView>()).Error);
            Assert.Zero(Assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator ClearInPresenterCallbackIsRejected() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            UIResult result = default;
            Probes[typeof(FirstPageView)].OnResume = () => result = UI.ClearAsync().GetAwaiter().GetResult();
            Assert.IsTrue((await UI.PushAsync<FirstPageView>()).Succeeded);
            Assert.AreEqual(UIError.ReentrantCall, result.Error);
        });

        [UnityTest]
        public IEnumerator DestroyedCoveredAndCachedWindowsDoNotRemoveLiveTop() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var first = await UI.PushAsync<FirstPageView>();
            var second = await UI.PushAsync<SecondPageView>();
            var hud = await UI.OpenAsync<NavigationHudView>();
            await UI.CloseAsync(hud.Handle);
            UnityEngine.Object.DestroyImmediate(first.View.gameObject);
            UnityEngine.Object.DestroyImmediate(hud.View.gameObject);
            await UniTask.Yield(); await UniTask.Yield();
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.Zero(UI.GetDiagnostics().ClosedCacheCount);
            Assert.IsTrue(second.View.CanvasGroup.interactable);
            Assert.AreEqual(1, Probes[typeof(FirstPageView)].Unbinds);
            Assert.AreEqual(1, Probes[typeof(NavigationHudView)].Unbinds);
            Assert.IsTrue((await UI.OpenAsync<NavigationHudView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator CleanupHandleRetriesAfterViewDestroyed() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var hud = await UI.OpenAsync<NavigationHudView>();
            Probes[typeof(NavigationHudView)].OnUnbind = () => throw new InvalidOperationException("destroyed cleanup failure");
            UnityEngine.Object.DestroyImmediate(hud.View.gameObject);
            await UniTask.Yield(); await UniTask.Yield();
            Assert.AreEqual(1, UI.GetDiagnostics().FailedCleanupCount);
            StringAssert.Contains("destroyed cleanup failure", UI.GetDiagnostics().LastRecoveryError);
            Probes[typeof(NavigationHudView)].OnUnbind = () => { };
            Assert.IsTrue((await UI.ReleaseAsync(hud.Handle)).Succeeded);
            Assert.Zero(UI.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator DestroyedEnteringCandidateCannotCommit() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(new UITransitionOptions(0.1f, true, Vector2.zero, 0.8f));
            var first = await UI.PushAsync<FirstPageView>();
            var opening = UI.PushAsync<SecondPageView>();
            UnityEngine.Object.DestroyImmediate(Views[typeof(SecondPageView)].gameObject);
            Exception failure = null;
            try { await opening; } catch (Exception error) { failure = error; }
            Assert.IsNotNull(failure);
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
            Assert.IsTrue((await UI.PushAsync<SecondPageView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator DestroyedPreviousPageDuringEntryIsNotRestoredBySnapshot() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(new UITransitionOptions(0.1f, true, Vector2.zero, 0.8f));
            var first = await UI.PushAsync<FirstPageView>();
            var opening = UI.PushAsync<SecondPageView>();
            UnityEngine.Object.DestroyImmediate(first.View.gameObject);
            Assert.IsTrue((await opening).Succeeded);
            await UniTask.Yield(); await UniTask.Yield();
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.AreEqual(1, UI.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator FocusCallbackDestroyingCandidateCannotReturnSuccess() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            NavigationSelectionProbe.SelectionChanged = () =>
            {
                if (Views.TryGetValue(typeof(FirstModalView), out var candidate) && candidate != null &&
                    Events.currentSelectedGameObject == candidate.DefaultSelection.gameObject)
                { UnityEngine.Object.DestroyImmediate(candidate.gameObject); }
            };
            Exception failure = null;
            try { await UI.OpenAsync<FirstModalView>(); } catch (Exception error) { failure = error; }
            NavigationSelectionProbe.SelectionChanged = null;
            Assert.IsNotNull(failure);
            Assert.IsFalse(UI.HasModal);
            Assert.IsTrue(page.View.CanvasGroup.interactable);
            Assert.AreEqual(1, UI.GetDiagnostics().WindowCount);
        });

        [UnityTest]
        public IEnumerator FailedClearNotificationKeepsStoppedUntilRetry() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            await UI.OpenAsync<FirstModalView>();
            Action<bool> notification = active => { if (!active) { throw new InvalidOperationException("clear notification failure"); } };
            UI.ModalStateChanged += notification;
            try { await UI.ClearAsync(); Assert.Fail("Expected failure."); } catch (UIOperationException) { }
            Assert.AreEqual(UIError.NotRunning, (await UI.PushAsync<FirstPageView>()).Error);
            UI.ModalStateChanged -= notification;
            Assert.IsTrue((await UI.ClearAsync()).Succeeded);
            Assert.IsTrue((await UI.PushAsync<FirstPageView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator ClearNotificationCanCloseCoreWithoutPublishingSuccess() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            await UI.OpenAsync<FirstModalView>();
            UniTask closing = default;
            UI.ModalStateChanged += active => { if (!active) { closing = Core.CloseAsync(); } };
            try { await UI.ClearAsync(); Assert.Fail("Expected interrupted notification."); } catch (UIOperationException) { }
            await closing;
            Assert.AreEqual(UIError.NotRunning, (await UI.OpenAsync<NavigationHudView>()).Error);
            Assert.Zero(Assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator ClearInShutdownUnbindStillRejectsReentrancy() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            await UI.OpenAsync<NavigationHudView>();
            UIResult rejected = default;
            Probes[typeof(NavigationHudView)].OnUnbind = () => rejected = UI.ClearAsync().GetAwaiter().GetResult();
            await Core.CloseAsync();
            Assert.AreEqual(UIError.ReentrantCall, rejected.Error);
        });

        [UnityTest]
        public IEnumerator NeverActivatedInstanceIsDetectedWithoutOnDestroy() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var run = (UIRun)typeof(UIModule).GetField("_run", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(UI);
            var scope = run.Scope.CreateScope("never-active");
            var instance = await scope.InstantiateAsync(Folder + "/NavigationHudView", run.Root.Staging);
            Assert.IsFalse(instance.Value.activeInHierarchy);
            var record = new UIWindowRecord(typeof(NavigationHudView), new UIHandle(run.Owner, run.Generation, 987, 1),
                scope, UIRole.Hud, false, false, UICachePolicy.Cache, default);
            record.Instance = instance;
            record.View = instance.Value.GetComponent<NavigationHudView>();
            run.Windows.Add(record.ViewType, record);
            run.Cache.Add(record, false);
            UnityEngine.Object.DestroyImmediate(instance.Value);
            await UniTask.Yield(); await UniTask.Yield(); await UniTask.Yield();
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.Zero(UI.GetDiagnostics().ClosedCacheCount);
            Assert.Zero(Assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator DestroyedTopModalRestoresPageAndNotifiesOnce() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            var modal = await UI.OpenAsync<FirstModalView>();
            int closed = 0;
            UI.ModalStateChanged += active => { if (!active) { closed++; } };
            UnityEngine.Object.DestroyImmediate(modal.View.gameObject);
            await UniTask.Yield(); await UniTask.Yield();
            Assert.IsFalse(UI.HasModal);
            Assert.AreEqual(1, closed);
            Assert.IsTrue(page.View.CanvasGroup.interactable);
            Assert.AreSame(page.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator ImmediateCloseOfDestroyedWindowCannotCacheDeadInstance() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(new UITransitionOptions(0.1f, true, Vector2.zero, 0.8f));
            var hud = await UI.OpenAsync<NavigationHudView>();
            UnityEngine.Object.DestroyImmediate(hud.View.gameObject);
            Assert.IsTrue((await UI.CloseAsync(hud.Handle)).Succeeded);
            Assert.Zero(UI.GetDiagnostics().ClosedCacheCount);
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.IsTrue((await UI.OpenAsync<NavigationHudView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator DestroyedCacheAndAdmissionCandidateAreReleased() => UniTask.ToCoroutine(async () =>
        {
            UI = new UIModule(new UIOptions(closedCacheCapacity: 1), Events);
            await StartAsync();
            var hud = await UI.OpenAsync<NavigationHudView>();
            await UI.CloseAsync(hud.Handle);
            UnityEngine.Object.DestroyImmediate(hud.View.gameObject);
            var firstClose = UI.CloseAsync(hud.Handle);
            var secondClose = UI.CloseAsync(hud.Handle);
            Assert.IsTrue((await firstClose).Succeeded);
            Assert.IsTrue((await secondClose).Succeeded);
            Assert.Zero(UI.GetDiagnostics().ClosedCacheCount);
            hud = await UI.OpenAsync<NavigationHudView>();
            await UI.CloseAsync(hud.Handle);
            var modal = await UI.OpenAsync<FirstModalView>();
            var closing = UI.CloseAsync(modal.Handle);
            Assert.AreEqual(UniTaskStatus.Pending, closing.Status);
            UnityEngine.Object.DestroyImmediate(modal.View.gameObject);
            Assert.IsTrue((await closing).Succeeded);
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.Zero(UI.GetDiagnostics().ClosedCacheCount);
            Assert.Zero(Assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator FailedDestructionRecoveryDoesNotNotifyModalClosureTwice() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            await UI.PushAsync<FirstPageView>();
            var second = await UI.PushAsync<SecondPageView>();
            await UI.OpenAsync<FirstModalView>();
            int closed = 0;
            UI.ModalStateChanged += active => { if (!active) { closed++; } };
            Probes[typeof(FirstPageView)].OnResume = () => throw new InvalidOperationException("recovery resume failed");
            UnityEngine.Object.DestroyImmediate(second.View.gameObject);
            for (int i = 0; i < 8; i++) { await UniTask.Yield(); }
            Assert.AreEqual(1, closed);
            Assert.IsFalse(UI.HasModal);
            Assert.Zero(UI.GetDiagnostics().PageDepth);
            StringAssert.Contains("recovery resume failed", UI.GetDiagnostics().LastRecoveryError);
        });

        [UnityTest]
        public IEnumerator DiagnosticsAreImmutableAndRecordObservedComponents() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var hud = await UI.OpenAsync<NavigationHudView>();
            var mask = hud.View.Visual.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            var snapshot = UI.GetDiagnostics();
            Assert.AreEqual(1, snapshot.StateCounts[UIWindowState.Visible]);
            Assert.AreEqual(1, snapshot.RectMaskCount);
            Assert.GreaterOrEqual(snapshot.GraphicCount, 2);
            Assert.AreEqual(1, snapshot.CanvasCount);
            Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<UIWindowDiagnostic>)snapshot.Windows).Clear());
            Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IDictionary<UIWindowState, int>)snapshot.StateCounts).Clear());
            Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<UIComponentDiagnostic>)snapshot.Windows[0].Components).Clear());
            var components = snapshot.Windows[0].Components;
            bool foundGraphic = false;
            foreach (var component in components)
            {
                Assert.AreEqual("Unknown", component.MaterialOrigin);
                if (component.Type == typeof(UnityEngine.UI.Image).FullName)
                { foundGraphic = true; Assert.AreNotEqual(0, component.MaterialId); Assert.AreNotEqual(0, component.TextureId); }
            }
            Assert.IsTrue(foundGraphic);
            await UI.CloseAsync(hud.Handle);
            Assert.Greater(Assets.GetDiagnostics().Resources.Count, 0);
            Assert.AreEqual(1, UI.GetDiagnostics().StateCounts[UIWindowState.CachedHidden]);
            Assert.AreEqual(UIWindowState.Visible, snapshot.Windows[0].State);
            await UI.ClearAsync();
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.Zero(Assets.GetDiagnostics().Resources.Count);
        });
    }
}
