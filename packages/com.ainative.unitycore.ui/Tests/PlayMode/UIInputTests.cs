using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UIInputTests : UITask5Fixture
    {
        [UnityTest]
        public IEnumerator DoubleModalRaycastAndSubmitReachOnlyTopAndMaskSwallowsOutside() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            var first = await UI.OpenAsync<FirstModalView>();
            var second = await UI.OpenAsync<SecondModalView>();
            int pageClicks = 0, firstClicks = 0, secondClicks = 0;
            ((Button)page.View.DefaultSelection).onClick.AddListener(() => pageClicks++);
            ((Button)first.View.DefaultSelection).onClick.AddListener(() => firstClicks++);
            ((Button)second.View.DefaultSelection).onClick.AddListener(() => secondClicks++);
            await UniTask.Yield();
            var hits = Hits(Center(second.View));
            Assert.AreSame(second.View.DefaultSelection.gameObject, hits[0].gameObject);
            Assert.IsFalse(hits.Exists(hit => hit.gameObject == first.View.DefaultSelection.gameObject));
            Assert.IsFalse(hits.Exists(hit => hit.gameObject == page.View.DefaultSelection.gameObject));
            ExecuteEvents.Execute(hits[0].gameObject, new PointerEventData(Events) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(Events.currentSelectedGameObject, new BaseEventData(Events), ExecuteEvents.submitHandler);
            Assert.AreEqual(2, secondClicks);
            Assert.Zero(firstClicks);
            Assert.Zero(pageClicks);
            var outside = Hits(new Vector2(1, 1));
            Assert.AreEqual("ModalMask", outside[0].gameObject.name);
            var mask = outside[0].gameObject;
            Assert.AreEqual(first.View.transform.GetSiblingIndex() + 1, mask.transform.GetSiblingIndex());
            Assert.AreEqual(mask.transform.GetSiblingIndex() + 1, second.View.transform.GetSiblingIndex());
            ExecuteEvents.Execute(mask, new PointerEventData(Events), ExecuteEvents.pointerClickHandler);
            Assert.AreEqual(2, UI.GetDiagnostics().ModalCount);
            await UI.CloseAsync(second.Handle);
            Assert.AreSame(first.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator OverlayRaycastsPassThroughToPage() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            var overlay = await UI.OpenAsync<UIView>();
            await UniTask.Yield();
            var hits = Hits(Center(page.View));
            Assert.AreSame(page.View.DefaultSelection.gameObject, hits[0].gameObject);
            Assert.IsFalse(hits.Exists(hit => hit.gameObject.transform.IsChildOf(overlay.View.transform)));
        });

        [UnityTest]
        public IEnumerator InvalidOldSelectionsFallBackToDefault() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            for (int mode = 0; mode < 3; mode++)
            {
                var other = Rect("Other", page.View.Visual).gameObject.AddComponent<Button>();
                Events.SetSelectedGameObject(other.gameObject);
                var modal = await UI.OpenAsync<FirstModalView>();
                if (mode == 0) { other.gameObject.SetActive(false); }
                else if (mode == 1) { other.interactable = false; }
                else { UnityEngine.Object.DestroyImmediate(other.gameObject); }
                await UI.CloseAsync(modal.Handle);
                Assert.AreSame(page.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
                if (other != null) { UnityEngine.Object.DestroyImmediate(other.gameObject); }
            }
        });

        [UnityTest]
        public IEnumerator ClosingFocusedHudClearsSelection() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var hud = await UI.OpenAsync<NavigationHudView>();
            Events.SetSelectedGameObject(hud.View.DefaultSelection.gameObject);
            await UI.CloseAsync(hud.Handle);
            Assert.IsNull(Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator InheritedCanvasPreservesModalInputAndSorting() => UniTask.ToCoroutine(async () =>
        {
            ModifyPrefab<FirstPageView>(view =>
            {
                var canvas = view.Visual.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = false;
                view.Visual.gameObject.AddComponent<GraphicRaycaster>();
            });
            await StartAsync();
            LogAssert.Expect(LogType.Warning, "UI window contains an additional Canvas; review sorting and batching manually.");
            var page = await UI.PushAsync<FirstPageView>();
            var extraCanvas = page.View.Visual.GetComponent<Canvas>();
            Assert.IsFalse(extraCanvas.overrideSorting);
            Assert.AreSame(page.View.GetComponentInParent<Canvas>(), extraCanvas.rootCanvas);
            await UniTask.Yield();
            Assert.AreSame(page.View.DefaultSelection.gameObject, Hits(Center(page.View))[0].gameObject);
            var modal = await UI.OpenAsync<FirstModalView>();
            await UniTask.Yield();
            var hits = Hits(Center(page.View));
            Assert.IsFalse(hits.Exists(hit => hit.gameObject.transform.IsChildOf(page.View.transform)));
            Assert.AreSame(modal.View.DefaultSelection.gameObject, hits[0].gameObject);
            Assert.IsFalse(extraCanvas.overrideSorting);
        });

        [UnityTest]
        public IEnumerator OverrideSortingFromActivationRollsBackBeforeDelivery() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            NavigationView.Activation = (view, active) =>
            {
                if (!active || !(view is SecondPageView)) { return; }
                var canvas = view.Visual.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = 99;
            };
            Exception failure = null;
            try { await UI.PushAsync<SecondPageView>(); }
            catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<InvalidOperationException>(failure);
            StringAssert.Contains("overrideSorting", failure.Message);
            Assert.IsFalse(UI.TryGet<SecondPageView>(out _, out _));
            Assert.AreEqual(1, UI.GetDiagnostics().WindowCount);
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.AreEqual(0, UI.GetDiagnostics().PendingOperations);
            Assert.AreEqual(1, Probes[typeof(SecondPageView)].Unbinds);
            Assert.AreSame(page.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
            Assert.IsTrue(page.View.CanvasGroup.interactable);
            NavigationView.Activation = null;
            Assert.IsTrue((await UI.PushAsync<SecondPageView>()).Succeeded);
        });

        [UnityTest]
        public IEnumerator InvalidSortingOnCacheReopenRetainsReleasableOldHandle() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var modal = await UI.OpenAsync<FirstModalView>();
            Assert.IsTrue((await UI.CloseAsync(modal.Handle)).Succeeded);
            NavigationView.Activation = (view, active) =>
            {
                if (!active || !(view is FirstModalView)) { return; }
                view.Visual.gameObject.AddComponent<Canvas>().overrideSorting = true;
            };
            Exception failure = null;
            try { await UI.OpenAsync<FirstModalView>(); }
            catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<InvalidOperationException>(failure);
            Assert.IsFalse(UI.HasModal);
            Assert.AreEqual(0, UI.GetDiagnostics().PendingOperations);
            Assert.AreEqual(1, UI.GetDiagnostics().ClosedCacheCount);
            Assert.IsFalse(modal.View.gameObject.activeSelf);
            Assert.IsTrue((await UI.ReleaseAsync(modal.Handle)).Succeeded);
            Assert.AreEqual(0, UI.GetDiagnostics().WindowCount);
            Assert.AreEqual(1, Probes[typeof(FirstModalView)].Unbinds);
        });

        [UnityTest]
        public IEnumerator InvalidSortingOnCoveredPageRestoreKeepsCurrentPageAndCanRetry() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var first = await UI.PushAsync<FirstPageView>();
            var second = await UI.PushAsync<SecondPageView>();
            Canvas invalidCanvas = null;
            NavigationView.Activation = (view, active) =>
            {
                if (!active || !(view is FirstPageView)) { return; }
                invalidCanvas = view.Visual.gameObject.AddComponent<Canvas>();
                invalidCanvas.overrideSorting = true;
            };
            UIOperationException failure = null;
            try { await UI.BackAsync(); }
            catch (UIOperationException error) { failure = error; }
            Assert.IsNotNull(failure);
            Assert.IsFalse(failure.Committed);
            StringAssert.Contains("overrideSorting", failure.ToString());
            Assert.AreEqual(2, UI.GetDiagnostics().PageDepth);
            Assert.AreEqual(0, UI.GetDiagnostics().PendingOperations);
            Assert.IsFalse(first.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.gameObject.activeSelf);
            Assert.IsTrue(second.View.CanvasGroup.interactable);
            Assert.AreSame(second.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
            NavigationView.Activation = null;
            // Explicitly remove the injected invalid configuration before retrying,
            // rather than relying on a property edit while the Canvas is inactive.
            UnityEngine.Object.DestroyImmediate(invalidCanvas);
            Assert.IsTrue((await UI.BackAsync()).Succeeded);
            Assert.AreEqual(1, UI.GetDiagnostics().PageDepth);
            Assert.IsTrue(first.View.gameObject.activeSelf);
            Assert.AreSame(first.View.DefaultSelection.gameObject, Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator ConfiguredDisplayOptionsReachCanvasAndModalMask() => UniTask.ToCoroutine(async () =>
        {
            var color = new Color(0.2f, 0.3f, 0.4f, 0.7f);
            UI = new UIModule(new UIOptions(referenceResolution: new Vector2(720, 1280),
                matchWidthOrHeight: 0, modalMaskColor: color), Events);
            await StartAsync();
            var modal = await UI.OpenAsync<FirstModalView>();
            var root = modal.View.GetComponentInParent<Canvas>();
            var scaler = root.GetComponent<CanvasScaler>();
            Assert.AreEqual(CanvasScaler.ScaleMode.ScaleWithScreenSize, scaler.uiScaleMode);
            Assert.AreEqual(new Vector2(720, 1280), scaler.referenceResolution);
            Assert.AreEqual(0, scaler.matchWidthOrHeight);
            var mask = root.transform.Find("Modal/ModalMask").GetComponent<Image>();
            Assert.AreEqual(color, mask.color);
            Assert.IsTrue(mask.gameObject.activeInHierarchy);
            Assert.IsTrue(mask.raycastTarget);
        });

        [UnityTest]
        public IEnumerator InvalidHierarchyAndInputBypassAreRejectedBeforeBinding() => UniTask.ToCoroutine(async () =>
        {
            ModifyPrefab<FirstPageView>(view => view.CanvasGroup.enabled = false);
            ModifyPrefab<FirstModalView>(view => view.Visual.GetComponent<CanvasGroup>().ignoreParentGroups = true);
            ModifyPrefab<NavigationHudView>(view =>
            {
                var serialized = new SerializedObject(view);
                serialized.FindProperty("_visual").objectReferenceValue = view.SafeArea;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            });
            await StartAsync();
            await ExpectInvalid(UI.PushAsync<FirstPageView>());
            await ExpectInvalid(UI.OpenAsync<FirstModalView>());
            await ExpectInvalid(UI.OpenAsync<NavigationHudView>());
            Assert.Zero(Views.Count);
            Assert.Zero(UI.GetDiagnostics().WindowCount);
            Assert.Zero(Assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator DestroyedDefaultSelectionDoesNotBreakModalClose() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync();
            var page = await UI.PushAsync<FirstPageView>();
            var modal = await UI.OpenAsync<FirstModalView>();
            UnityEngine.Object.DestroyImmediate(page.View.DefaultSelection.gameObject);
            Assert.IsTrue((await UI.CloseAsync(modal.Handle)).Succeeded);
            Assert.IsNull(Events.currentSelectedGameObject);
            Assert.IsTrue(page.View.CanvasGroup.interactable);
        });

        [UnityTest]
        public IEnumerator ModalTransitionRaycastsStayOnMaskUntilCommitted() => UniTask.ToCoroutine(async () =>
        {
            await StartAsync(new UITransitionOptions(0.25f, true, Vector2.zero, 0.8f));
            var page = await UI.PushAsync<FirstPageView>();
            var opening = UI.OpenAsync<FirstModalView>();
            await UniTask.Yield();
            var duringEnter = Hits(Center(page.View));
            Assert.AreEqual("ModalMask", duringEnter[0].gameObject.name);
            var modal = await opening;
            Assert.AreSame(modal.View.DefaultSelection.gameObject, Hits(Center(modal.View))[0].gameObject);
            var closing = UI.CloseAsync(modal.Handle);
            Assert.AreEqual("ModalMask", Hits(Center(page.View))[0].gameObject.name);
            await closing;
            Assert.AreSame(page.View.DefaultSelection.gameObject, Hits(Center(page.View))[0].gameObject);
        });

        private static async UniTask ExpectInvalid<T>(UniTask<UIOpenResult<T>> opening) where T : UIView
        {
            try { await opening; Assert.Fail("Expected invalid prefab."); }
            catch (InvalidOperationException) { }
        }

        private static void ModifyPrefab<T>(Action<T> change) where T : UIView
        {
            string path = Folder + "/" + typeof(T).Name + ".prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try { change(root.GetComponent<T>()); PrefabUtility.SaveAsPrefabAsset(root, path); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static Vector2 Center(UIView view) => RectTransformUtility.WorldToScreenPoint(null, view.DefaultSelection.transform.position);
        private List<RaycastResult> Hits(Vector2 point)
        {
            Canvas.ForceUpdateCanvases();
            var hits = new List<RaycastResult>();
            Events.RaycastAll(new PointerEventData(Events) { position = point }, hits);
            Assert.IsNotEmpty(hits);
            return hits;
        }
    }
}
