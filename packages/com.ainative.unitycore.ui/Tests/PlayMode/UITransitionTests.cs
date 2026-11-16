using System;
using System.Collections;
using System.Threading;
using System.Reflection;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UITransitionTests : UITask5Fixture
    {
        private static UITransitionOptions Animated(float seconds = 0.12f) =>
            new UITransitionOptions(seconds, true, new Vector2(80, -30), 0.5f);

        [UnityTest]
        public IEnumerator StableScreenUpdatesDoNotAllocateManagedMemory() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            await UI.OpenAsync<NavigationHudView>();
            var update = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), Run,
                typeof(UIRun).GetMethod("UpdateScreen", BindingFlags.Instance | BindingFlags.NonPublic));
            // Warm up dimension application and collection access outside the measured interval.
            for (int i = 0; i < 8; i++) { update(0); }
            using var allocations = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Internal,
                "GC.Alloc", 4096, Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            GC.KeepAlive(new byte[1024]);
            allocations.Stop();
            Assert.Greater(allocations.Count, 0, "The allocation recorder must detect a positive control.");
            allocations.Reset();
            allocations.Start();
            for (int i = 0; i < 256; i++) { update(0); }
            allocations.Stop();
            int allocated = allocations.Count;
            Assert.Zero(allocated, "Stable UI screen/destruction observation should not allocate per frame.");
        });

        [UnityTest]
        public IEnumerator ConcurrentHudUsesCommitOrder() => UniTask.ToCoroutine(() => CheckCommitOrder(UIRole.Hud, false));

        [UnityTest]
        public IEnumerator CachedHudUsesCommitOrder() => UniTask.ToCoroutine(() => CheckCommitOrder(UIRole.Hud, true));

        [UnityTest]
        public IEnumerator ConcurrentOverlayUsesCommitOrder() => UniTask.ToCoroutine(() => CheckCommitOrder(UIRole.Overlay, false));

        [UnityTest]
        public IEnumerator CachedOverlayUsesCommitOrder() => UniTask.ToCoroutine(() => CheckCommitOrder(UIRole.Overlay, true));

        private async UniTask CheckCommitOrder(UIRole role, bool cached)
        {
            Register<FirstPageView>(role, Animated(0.35f));
            Register<SecondPageView>(role);
            Core.RegisterModule(Assets).RegisterModule(UI);
            await Core.RunAsync();
            UIView original = null;
            if (cached)
            {
                var first = await UI.OpenAsync<FirstPageView>();
                original = first.View;
                var second = await UI.OpenAsync<SecondPageView>();
                await UI.CloseAsync(first.Handle);
                await UI.CloseAsync(second.Handle);
            }
            var slower = UI.OpenAsync<FirstPageView>();
            var faster = await UI.OpenAsync<SecondPageView>();
            Assert.AreEqual(UniTaskStatus.Pending, slower.Status);
            var last = await slower;
            Assert.IsTrue(last.Succeeded && faster.Succeeded);
            Assert.AreSame(last.View.transform.parent, faster.View.transform.parent);
            Assert.Greater(last.View.transform.GetSiblingIndex(), faster.View.transform.GetSiblingIndex());
            if (cached) { Assert.AreSame(original, last.View); }
            Assert.IsTrue(UI.BringToFront(faster.Handle).Succeeded);
            Assert.Greater(faster.View.transform.GetSiblingIndex(), last.View.transform.GetSiblingIndex());
            Assert.AreEqual(role == UIRole.Hud, last.View.CanvasGroup.interactable);
        }

        [UnityTest]
        public IEnumerator TransitionCompletesAtTimeScaleZero() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            Time.timeScale = 0;
            var opening = UI.OpenAsync<NavigationHudView>();
            Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
            var view = Views[typeof(NavigationHudView)];
            Assert.IsFalse(view.CanvasGroup.interactable);
            Assert.IsFalse(UI.TryGet<NavigationHudView>(out _, out _));
            var opened = await opening.Timeout(TimeSpan.FromSeconds(3), DelayType.Realtime);
            Assert.IsTrue(opened.Succeeded);
            AssertBaseline(view);
            Assert.IsTrue(view.CanvasGroup.interactable);
        });

        [UnityTest]
        public IEnumerator CancelledEnterRestoresAllBaselineProperties() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            var first = await UI.OpenAsync<NavigationHudView>();
            await UI.CloseAsync(first.Handle);
            using var cancellation = new CancellationTokenSource();
            var opening = UI.OpenAsync<NavigationHudView>(cancellation.Token);
            Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
            cancellation.Cancel();
            try { await opening; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            AssertBaseline(first.View);
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.AreEqual(1, UI.GetDiagnostics().ClosedCacheCount);
            Assert.IsTrue((await UI.ReleaseAsync(first.Handle)).Succeeded);
        });

        [UnityTest]
        public IEnumerator EnterKeepsPreviousPageAndBlocksInputUntilCommit() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            var first = await UI.PushAsync<FirstPageView>();
            var opening = UI.PushAsync<SecondPageView>();
            Assert.AreEqual(UniTaskStatus.Pending, opening.Status);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsFalse(first.View.CanvasGroup.interactable);
            Assert.IsFalse(first.View.CanvasGroup.blocksRaycasts);
            Assert.IsNull(Events.currentSelectedGameObject);
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.AreEqual(UIError.NavigationBusy, (await UI.BackAsync()).Error);
            var second = await opening;
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.AreSame(second.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
            var closing = UI.CloseAsync(second.Handle);
            Assert.AreEqual(UniTaskStatus.Pending, closing.Status);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.gameObject.activeSelf);
            Assert.IsFalse(first.View.CanvasGroup.interactable);
            Assert.IsTrue((await closing).Succeeded);
            AssertBaseline(second.View);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
        });

        [UnityTest]
        public IEnumerator CompletedTransitionReleasesTicket() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            var opening = UI.OpenAsync<NavigationHudView>();
            var transition = Run.Windows[typeof(NavigationHudView)].Transition;
            var ticket = Ticket(transition);
            Assert.IsTrue(ticket.Id.HasValue);
            var opened = await opening;
            Assert.IsFalse(ticket.Id.HasValue);
            Assert.IsNull(Run.Windows[typeof(NavigationHudView)].Transition);
            opened.View.Visual.anchoredPosition = new Vector2(12, 34);
            await UniTask.Yield();
            await UniTask.Yield();
            Assert.AreEqual(new Vector2(12, 34), opened.View.Visual.anchoredPosition);
        });

        [UnityTest]
        public IEnumerator ShutdownDoesNotWaitForTransitionTick() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated(30));
            var opening = UI.OpenAsync<NavigationHudView>();
            var view = Views[typeof(NavigationHudView)];
            var ticket = Ticket(Run.Windows[typeof(NavigationHudView)].Transition);
            // Core may stop its dispatch before shutdown. Cancellation must finish
            // the transition without another scheduler tick.
            Core.TimeMachine.ClearAll();
            var closing = Core.CloseAsync();
            AssertBaseline(view);
            Assert.IsFalse(ticket.Id.HasValue);
            try { await opening; Assert.Fail("Expected shutdown cancellation."); }
            catch (OperationCanceledException) { }
            await closing.Timeout(TimeSpan.FromSeconds(3), DelayType.Realtime);
            Assert.IsTrue(view == null);
            Assert.Zero(Assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator CoreRetirementCancelsTransitionAndReleasesTicketsSynchronously() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated(30));
            var opening = UI.OpenAsync<NavigationHudView>();
            var run = Run;
            var transitionTicket = Ticket(run.Windows[typeof(NavigationHudView)].Transition);
            var screenTicket = (ITimeTicket)typeof(UIRun).GetField("_screenTicket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(run);
            AiNative.UnityCore.Core.RetireCurrent();
            Assert.IsFalse(transitionTicket.Id.HasValue);
            Assert.IsFalse(screenTicket.Id.HasValue);
            try { await opening; Assert.Fail("Expected retirement cancellation."); }
            catch (OperationCanceledException) { }
            await Core.CloseAsync();
            Assert.Zero(Assets.GetDiagnostics().Scopes.Count);
        });

        [UnityTest]
        public IEnumerator CancelledPageEnterRestoresPageInputAndFocus() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated(30));
            var first = await UI.PushAsync<FirstPageView>();
            using var cancellation = new CancellationTokenSource();
            var opening = UI.PushAsync<SecondPageView>(cancellation.Token);
            var candidate = Views[typeof(SecondPageView)];
            cancellation.Cancel();
            try { await opening; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert.IsTrue(candidate == null);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.IsTrue(first.View.CanvasGroup.interactable);
            Assert.AreSame(first.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
        });

        [UnityTest]
        public IEnumerator FailedDestinationResumeRestoresOutgoingPageBeforeExit() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            var first = await UI.PushAsync<FirstPageView>();
            var second = await UI.PushAsync<SecondPageView>();
            Probes[typeof(FirstPageView)].OnResume = () => throw new InvalidOperationException("destination resume failed");
            try { await UI.CloseAsync(second.Handle); Assert.Fail("Expected close rollback."); }
            catch (UIOperationException error) { Assert.IsFalse(error.Committed); }
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.CanvasGroup.interactable);
            AssertBaseline(second.View);
            Assert.AreEqual(2, UI.GetDiagnostics().PageDepth);
            Probes[typeof(FirstPageView)].OnResume = () => { };
            Assert.IsTrue((await UI.CloseAsync(second.Handle)).Succeeded);
        });

        [UnityTest]
        public IEnumerator ClosingLowerModalKeepsTopAndMaskOrderDuringAnimation() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            var first = await UI.OpenAsync<FirstModalView>();
            var second = await UI.OpenAsync<SecondModalView>();
            int firstIndex = first.View.transform.GetSiblingIndex();
            int secondIndex = second.View.transform.GetSiblingIndex();
            var closing = UI.CloseAsync(first.Handle);
            Assert.AreEqual(UniTaskStatus.Pending, closing.Status);
            Assert.AreEqual(firstIndex, first.View.transform.GetSiblingIndex());
            Assert.AreEqual(secondIndex, second.View.transform.GetSiblingIndex());
            Assert.IsFalse(second.View.CanvasGroup.interactable);
            await closing;
            Assert.IsTrue(second.View.CanvasGroup.interactable);
            Assert.AreSame(second.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator ScreenUpdatesDoNotRewriteUnchangedGeometryAndShutdownDisposesTicket() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var opened = await UI.OpenAsync<NavigationHudView>();
            await UniTask.Yield();
            await UniTask.Yield();
            var ticket = (ITimeTicket)typeof(UIRun).GetField("_screenTicket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Run);
            var min = opened.View.SafeArea.anchorMin;
            var max = opened.View.SafeArea.anchorMax;
            Assert.AreEqual(UISafeArea.Normalize(Screen.width, Screen.height, Screen.safeArea).min, min);
            Assert.AreEqual(UISafeArea.Normalize(Screen.width, Screen.height, Screen.safeArea).max, max);
            opened.View.SafeArea.anchorMin = new Vector2(0.25f, 0.25f);
            await UniTask.Yield();
            await UniTask.Yield();
            Assert.AreEqual(new Vector2(0.25f, 0.25f), opened.View.SafeArea.anchorMin);
            await Core.CloseAsync();
            Assert.IsFalse(ticket.Id.HasValue);
        });

        [UnityTest]
        public IEnumerator HudCompletingDuringModalEntryDoesNotRestoreInputOrRemoveMask() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated(30), animateHud: false);
            var page = await UI.PushAsync<FirstPageView>();
            using var cancellation = new CancellationTokenSource();
            var opening = UI.OpenAsync<FirstModalView>(cancellation.Token);
            var modal = Views[typeof(FirstModalView)];
            var hud = await UI.OpenAsync<NavigationHudView>();
            Assert.IsFalse(hud.View.CanvasGroup.interactable);
            Assert.IsFalse(page.View.CanvasGroup.interactable);
            Assert.IsFalse(modal.CanvasGroup.interactable);
            Assert.IsNull(Events.currentSelectedGameObject);
            Assert.IsTrue(modal.transform.parent.Find("ModalMask").gameObject.activeSelf);
            cancellation.Cancel();
            try { await opening; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert.IsTrue(page.View.CanvasGroup.interactable);
            Assert.IsTrue(hud.View != null);
            Assert.IsFalse(UI.HasModal);
        });

        [UnityTest]
        public IEnumerator RepeatedAnimatedCloseAndImmediateContinuationShareCompletion() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(Animated());
            var modal = await UI.OpenAsync<FirstModalView>();
            var first = UI.CloseAsync(modal.Handle);
            var second = UI.CloseAsync(modal.Handle);
            Assert.IsTrue((await first).Succeeded);
            var next = await UI.OpenAsync<SecondModalView>();
            Assert.IsTrue(next.Succeeded);
            Assert.IsTrue((await second).Succeeded);
            Assert.AreEqual(1, Probes[typeof(FirstModalView)].Suspends);
        });

        [UnityTest]
        public IEnumerator SafeAreaResizeCallbackCanRetireCoreWithoutLateGeometryWrites() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var hud = await UI.OpenAsync<NavigationHudView>();
            hud.View.SafeArea.gameObject.AddComponent<UISizeProbe>();
            hud.View.SafeArea.anchorMin = new Vector2(0.2f, 0.2f);
            typeof(UIRun).GetField("_screenWidth", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Run, -1);
            bool retired = false;
            UISizeProbe.Changed = () =>
            {
                if (retired) { return; }
                retired = true;
                AiNative.UnityCore.Core.RetireCurrent();
            };
            try { Core.TimeMachine.OnUpdate(0); }
            finally { UISizeProbe.Changed = null; }
            Assert.IsTrue(retired);
            await Core.CloseAsync();
            Assert.IsTrue(hud.View == null);
            Assert.Zero(Assets.GetDiagnostics().Scopes.Count);
        });

        private UIRun Run => (UIRun)typeof(UIModule).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(UI);
        private static ITimeTicket Ticket(UITransition transition) =>
            (ITimeTicket)typeof(UITransition).GetField("_ticket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(transition);

        private static void AssertBaseline(UIView view)
        {
            Assert.AreEqual(0.8f, view.Visual.GetComponent<CanvasGroup>().alpha, 0.0001f);
            Assert.AreEqual(new Vector2(3, 4), view.Visual.anchoredPosition);
            Assert.AreEqual(new Vector3(0.9f, 0.8f, 1), view.Visual.localScale);
        }
    }
}
