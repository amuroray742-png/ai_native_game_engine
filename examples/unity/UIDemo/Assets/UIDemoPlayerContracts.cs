using System;
using System.Collections.Generic;
using System.Linq;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AiNative.UIDemo
{
    internal static class UIDemoPlayerContracts
    {
        internal static UIOptions Options => new UIOptions(closedCacheCapacity: 1,
            referenceResolution: new Vector2(2000, 1125), matchWidthOrHeight: 0.25f,
            modalMaskColor: new Color(0.1f, 0.2f, 0.3f, 0.65f));

        internal static async UniTask VerifyAsync(UIDemoBootstrap host, Action<string> passed)
        {
            Require(host.UI.TryGet<HomeView>(out var home, out _), "Home missing");
            var root = home.GetComponentInParent<Canvas>();
            var scaler = root.GetComponent<CanvasScaler>();
            var modal = await host.UI.OpenAsync<FirstModalView>();
            Require(modal.Succeeded, "Configuration modal open");
            var mask = root.transform.Find("Modal/ModalMask").GetComponent<Image>();
            Require(scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                scaler.referenceResolution == Options.ReferenceResolution &&
                scaler.matchWidthOrHeight == Options.MatchWidthOrHeight && mask.color == Options.ModalMaskColor &&
                mask.raycastTarget && mask.gameObject.activeInHierarchy, "Display options were not applied");
            passed("configured-display-options");
            Require((await host.UI.CloseAsync(modal.Handle)).Succeeded, "Configuration modal close");

            // Add the supported hierarchy while hidden, then reopen through normal validation.
            var inherited = modal.View.Visual.gameObject.AddComponent<Canvas>();
            inherited.overrideSorting = false;
            modal.View.Visual.gameObject.AddComponent<GraphicRaycaster>();
            modal = await host.UI.OpenAsync<FirstModalView>();
            Require(modal.Succeeded && !inherited.overrideSorting && inherited.rootCanvas == root,
                "Child Canvas must inherit root sorting");
            var command = modal.View.Commands.Single(x => x.Action == DemoAction.SecondModal);
            Require(Hits(host, command.Button).First().gameObject == command.Button.gameObject,
                "Inherited Canvas cannot receive input");
            var top = await host.UI.OpenAsync<SecondModalView>();
            Require(top.Succeeded && !Hits(host, command.Button).Any(x => x.gameObject.transform.IsChildOf(modal.View.transform)),
                "Covered inherited Canvas leaked input");
            Require((await host.UI.CloseAsync(top.Handle)).Succeeded && (await host.UI.CloseAsync(modal.Handle)).Succeeded,
                "Inherited Canvas close");
            passed("inherited-canvas-modal-input");
            // Give the next scenario a fresh instance. GraphicRaycaster requires Canvas;
            // deferred component destruction must not determine the injection result.
            Require((await host.UI.ReleaseAsync(modal.Handle)).Succeeded && modal.View == null,
                "Inherited Canvas scenario cleanup");
            modal = await host.UI.OpenAsync<FirstModalView>();
            Require(modal.Succeeded && (await host.UI.CloseAsync(modal.Handle)).Succeeded,
                "Fresh cached activation setup");
            modal.View.gameObject.AddComponent<UIDemoSortingActivation>();
            Exception failure = null;
            try { await host.UI.OpenAsync<FirstModalView>(); }
            catch (Exception error) { failure = error; }
            Require(failure is InvalidOperationException && failure.Message.Contains("overrideSorting"),
                "Cached activation did not reject illegal sorting");
            var diagnostics = host.UI.GetDiagnostics();
            Require(!host.UI.HasModal && !modal.View.gameObject.activeSelf && diagnostics.ClosedCacheCount == 1 &&
                diagnostics.PendingOperations == 0 && diagnostics.FailedCleanupCount == 0 &&
                host.Events.currentSelectedGameObject == home.DefaultSelection.gameObject,
                "Cached activation rollback lost state or focus");
            Require((await host.UI.ReleaseAsync(modal.Handle)).Succeeded && modal.View == null,
                "Old cached handle must release rejected candidate");
            passed("invalid-cached-canvas-rollback");

            var gallery = await host.UI.PushAsync<GalleryView>();
            Require(gallery.Succeeded && !home.gameObject.activeSelf, "Covered page setup");
            var injection = home.gameObject.AddComponent<UIDemoSortingActivation>();
            UIOperationException backFailure = null;
            try { await host.UI.BackAsync(); }
            catch (UIOperationException error) { backFailure = error; }
            Require(backFailure != null && !backFailure.Committed && backFailure.ToString().Contains("overrideSorting"),
                "Covered restore must reject before commit");
            diagnostics = host.UI.GetDiagnostics();
            Require(diagnostics.PageDepth == 2 && diagnostics.PendingOperations == 0 && diagnostics.FailedCleanupCount == 0 &&
                !home.gameObject.activeSelf && gallery.View.gameObject.activeSelf && gallery.View.CanvasGroup.interactable &&
                host.Events.currentSelectedGameObject == gallery.View.DefaultSelection.gameObject,
                "Back failure did not preserve stack and focus");
            UnityEngine.Object.Destroy(injection.Injected);
            UnityEngine.Object.Destroy(injection);
            await UniTask.NextFrame();
            Require((await host.UI.BackAsync()).Succeeded && host.UI.GetDiagnostics().PageDepth == 1 &&
                home.gameObject.activeSelf && host.Events.currentSelectedGameObject == home.DefaultSelection.gameObject,
                "Back retry failed after removing injected configuration");
            passed("invalid-covered-canvas-back-retry");
        }

        private static List<RaycastResult> Hits(UIDemoBootstrap host, Button button)
        {
            Canvas.ForceUpdateCanvases();
            var rect = button.GetComponent<RectTransform>();
            var pointer = new PointerEventData(host.Events)
            { position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center)) };
            var hits = new List<RaycastResult>();
            host.Events.RaycastAll(pointer, hits);
            Require(hits.Count > 0, "Contract raycast missed all graphics");
            return hits;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }
    }
}
