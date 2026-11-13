using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets.Editor;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AiNative.UIDemo.Tests
{
    public sealed class UIDemoTests
    {
        private UIDemoBootstrap _host;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            Core.ResetForPlay();
            _host = new GameObject("Test demo host").AddComponent<UIDemoBootstrap>();
            _host.AutoStart = false;
            _host.Events = new GameObject("Demo EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
            _host.Events.transform.SetParent(_host.transform);
            _host.Status = Text("Status"); _host.Diagnostics = Text("Diagnostics");
            _host.Commands = Array.Empty<DemoCommand>();
            var groups = AssetDatabase.LoadAssetAtPath<AssetGroups>("Assets/ResourceGroups.asset");
            Assert.AreEqual(EditorAssetLoadMode.AssetBundle, groups.EditorLoadMode);
            await _host.InitializeAsync(EditorAssets.CreateConfigured(groups));
            Assert.Greater(_host.Assets.GetDiagnostics().Bundles.Count, 0, "Must load real deployed bundles.");
        });

        private Text Text(string name)
        {
            var text = new GameObject(name, typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            text.transform.SetParent(_host.transform); return text;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            await _host.CloseAsync();
            Assert.Zero(_host.Assets.GetDiagnostics().Bundles.Count);
            Assert.Zero(_host.Assets.GetDiagnostics().PendingOperations);
            UnityEngine.Object.Destroy(_host.gameObject);
            await UniTask.Yield();
            Core.ResetForPlay();
        });

        [UnityTest]
        public IEnumerator RaycastNavigatesThreePagesAndTransparentBack() => UniTask.ToCoroutine(async () =>
        {
            await Click<HomeView>(DemoAction.Back);
            Assert.AreEqual("NoBackTarget", _host.LastResult);
            Assert.IsTrue(_host.UI.TryGet<HomeView>(out _, out _));
            await Click<HomeView>(DemoAction.Duplicate);
            Assert.AreEqual("AlreadyOpen", _host.LastResult);
            await Click<HomeView>(DemoAction.Next);
            await Click<GalleryView>(DemoAction.Third);
            await Click<DetailsView>(DemoAction.Transparent);
            Assert.AreEqual(4, _host.UI.GetDiagnostics().PageDepth);
            Assert.IsTrue(_host.UI.TryGet<DetailsView>(out var behind, out _));
            Assert.IsTrue(behind.gameObject.activeInHierarchy);
            await Click<TransparentView>(DemoAction.Back);
            Assert.AreEqual(3, _host.UI.GetDiagnostics().PageDepth);
            await Click<DetailsView>(DemoAction.Back);
            await Click<GalleryView>(DemoAction.Back);
            Assert.AreEqual(1, _host.UI.GetDiagnostics().PageDepth);
        });

        [UnityTest]
        public IEnumerator RaycastDoubleModalBlocksUnderlyingAndRestoresFocus() => UniTask.ToCoroutine(async () =>
        {
            await Click<HomeView>(DemoAction.Modal);
            Assert.IsTrue(_host.UI.TryGet<HomeView>(out var home, out _));
            Assert.AreNotEqual(Command(home, DemoAction.Next).Button.gameObject, Hit(Command(home, DemoAction.Next)));
            await Click<FirstModalView>(DemoAction.SecondModal);
            Assert.AreEqual(2, _host.UI.GetDiagnostics().ModalCount);
            await Click<SecondModalView>(DemoAction.Back);
            Assert.AreEqual(1, _host.UI.GetDiagnostics().ModalCount);
            await Click<FirstModalView>(DemoAction.Back);
            Assert.IsFalse(_host.UI.HasModal);
            Assert.AreEqual(home.DefaultSelection.gameObject, _host.Events.currentSelectedGameObject);
        });

        [UnityTest]
        public IEnumerator HudToastAndCachedSessionUseRealRaycasts() => UniTask.ToCoroutine(async () =>
        {
            Assert.IsTrue(_host.UI.TryGet<HudView>(out var first, out var old));
            await Click<HudView>(DemoAction.Toast);
            Assert.IsTrue(_host.UI.TryGet<ToastView>(out _, out var toast));
            await Click<HomeView>(DemoAction.Next);
            await _host.UI.CloseAsync(toast);
            await Click<HudView>(DemoAction.Hud);
            var reopened = await _host.UI.OpenAsync<HudView>();
            Assert.AreSame(first, reopened.View);
            Assert.AreNotEqual(old, reopened.Handle);
            Assert.AreEqual(UIError.InvalidHandle, (await _host.UI.CloseAsync(old)).Error);
        });

        [UnityTest]
        public IEnumerator RealBundlesShareImageAndTrimReleasesLastCacheOwner() => UniTask.ToCoroutine(async () =>
        {
            Assert.IsTrue(_host.UI.TryGet<HomeView>(out var home, out _));
            Assert.IsTrue(_host.UI.TryGet<HudView>(out var hud, out _));
            var image = _host.Model.Image;
            Assert.AreSame(image, home.Picture.texture);
            Assert.AreSame(image, hud.Picture.texture);
            Assert.AreSame(image, home.PrefabImage, "Bundle prefab dependency and dynamic lease must share identity.");
            Assert.AreSame(image, hud.PrefabImage);
            _host.Model.ReleaseImage();
            Assert.IsTrue(image != null);
            await _host.UI.BackAsync(); // The root must not be removed by Back.
            Assert.AreEqual(1, _host.UI.GetDiagnostics().PageDepth);
            await _host.UI.ClearAsync();
            Assert.Zero(_host.Assets.GetDiagnostics().Bundles.Count);
            // A fresh lease supplies the presenter; release it only after both windows bind.
            await _host.ReplaceSessionAsync();
            var first = await _host.UI.OpenAsync<FirstModalView>();
            var shared = first.View.Picture.texture;
            await _host.UI.CloseAsync(first.Handle);
            Assert.IsTrue(shared != null);
            Assert.AreEqual(1, _host.UI.GetDiagnostics().ClosedCacheCount);
            // Clear non-cache owners, then isolate one cached HUD without any other references.
            await _host.UI.ClearAsync();
            var only = await _host.UI.OpenAsync<HudView>();
            shared = only.View.Picture.texture;
            _host.Model.ReleaseImage();
            await _host.UI.CloseAsync(only.Handle);
            Assert.IsTrue(shared != null);
            Assert.Greater(_host.Assets.GetDiagnostics().Bundles.Count, 0);
            await _host.UI.TrimAsync();
            Assert.IsTrue(shared == null);
            Assert.Zero(_host.Assets.GetDiagnostics().Bundles.Count);
            Assert.Zero(_host.Assets.GetDiagnostics().Resources.Count);
        });

        [UnityTest]
        public IEnumerator ModelLeaseOutlivesAllWindowsAndReleasesFinalBundle() => UniTask.ToCoroutine(async () =>
        {
            var image = _host.Model.Image;
            await _host.UI.ClearAsync();
            Assert.IsTrue(image != null);
            Assert.Greater(_host.Assets.GetDiagnostics().Bundles.Count, 0);
            _host.Model.ReleaseImage();
            await UniTask.Yield();
            Assert.IsTrue(image == null);
            Assert.Zero(_host.Assets.GetDiagnostics().Bundles.Count);
        });

        [UnityTest]
        public IEnumerator CapacityOneEvictsLeastRecentBundleInstance() => UniTask.ToCoroutine(async () =>
        {
            var first = await _host.UI.OpenAsync<FirstModalView>();
            await _host.UI.CloseAsync(first.Handle);
            var same = await _host.UI.OpenAsync<FirstModalView>();
            Assert.AreSame(first.View, same.View);
            await _host.UI.CloseAsync(same.Handle);
            var second = await _host.UI.OpenAsync<SecondModalView>();
            await _host.UI.CloseAsync(second.Handle);
            Assert.IsTrue(first.View == null);
            Assert.AreEqual(1, _host.UI.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.InvalidHandle, (await _host.UI.ReleaseAsync(first.Handle)).Error);
        });

        [UnityTest]
        public IEnumerator AnimationCancellationRetainsPriorPageAndReportsCancellation() => UniTask.ToCoroutine(async () =>
        {
            await Click<HomeView>(DemoAction.CancelTransition);
            StringAssert.Contains("Cancelled", _host.LastResult);
            Assert.AreEqual(1, _host.UI.GetDiagnostics().PageDepth);
            Assert.IsTrue(_host.UI.TryGet<HomeView>(out _, out _));
            Assert.IsFalse(_host.UI.TryGet<GalleryView>(out _, out _));
            Assert.Zero(_host.UI.GetDiagnostics().PendingOperations);
        });

        [UnityTest]
        public IEnumerator FailedClearKeepsModelThenReplacesWholeSession() => UniTask.ToCoroutine(async () =>
        {
            var previous = _host.Model;
            _host.UI.TryGet<HomeView>(out _, out var old);
            await _host.ExecuteAsync(DemoAction.Next);
            var modal = await _host.UI.OpenAsync<FirstModalView>();
            await _host.UI.CloseAsync(modal.Handle);
            previous.FailNextUnbind = true;
            Exception failure = null;
            try { await _host.ReplaceSessionAsync(); } catch (UIOperationException error) { failure = error; }
            Assert.IsNotNull(failure);
            Assert.AreSame(previous, _host.Model);
            Assert.IsFalse(previous.IsDisposed);
            Assert.AreEqual(UIError.NotRunning, (await _host.UI.PushAsync<HomeView>()).Error);
            await _host.ReplaceSessionAsync();
            Assert.IsTrue(previous.IsDisposed);
            Assert.AreNotSame(previous, _host.Model);
            Assert.AreEqual(1, _host.UI.GetDiagnostics().PageDepth);
            Assert.Zero(_host.UI.GetDiagnostics().ClosedCacheCount);
            Assert.AreEqual(UIError.InvalidHandle, (await _host.UI.ReleaseAsync(old)).Error);
            Assert.IsTrue(_host.UI.TryGet<HomeView>(out var home, out _));
            StringAssert.Contains("SESSION 2", home.Caption.text);
        });

        [UnityTest]
        public IEnumerator ToastAutoClosesAndOldSessionDeadlineCannotCloseNewToast() => UniTask.ToCoroutine(async () =>
        {
            await _host.ExecuteAsync(DemoAction.Toast);
            Assert.IsTrue(_host.UI.TryGet<ToastView>(out _, out _));
            await UniTask.Delay(TimeSpan.FromSeconds(2.5), ignoreTimeScale: true);
            Assert.IsFalse(_host.UI.TryGet<ToastView>(out _, out _));
            Assert.Zero(_host.UI.GetDiagnostics().PendingOperations);
            await _host.ExecuteAsync(DemoAction.Toast);
            float oldDeadline = Time.unscaledTime + 2;
            await _host.ExecuteAsync(DemoAction.Clear);
            await _host.ExecuteAsync(DemoAction.Toast);
            Assert.IsTrue(_host.UI.TryGet<ToastView>(out var current, out _));
            await UniTask.WaitUntil(() => Time.unscaledTime > oldDeadline + 0.1f);
            Assert.IsTrue(_host.UI.TryGet<ToastView>(out var afterOldDeadline, out _));
            Assert.AreSame(current, afterOldDeadline);
            await UniTask.Delay(TimeSpan.FromSeconds(1.5), ignoreTimeScale: true);
            Assert.IsFalse(_host.UI.TryGet<ToastView>(out _, out _));
            Assert.Zero(_host.UI.GetDiagnostics().PendingOperations);
        });

        [UnityTest]
        public IEnumerator HostDisplaysCleanupExceptionAndAllowsClearRetry() => UniTask.ToCoroutine(async () =>
        {
            var previous = _host.Model;
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: Demo requested one cleanup failure"));
            _host.Dispatch(DemoAction.FaultClear);
            await UniTask.WaitUntil(() => !_host.Busy);
            Assert.IsInstanceOf<UIOperationException>(_host.LastError);
            StringAssert.Contains("retry", _host.LastResult);
            Assert.IsFalse(previous.IsDisposed);
            _host.Dispatch(DemoAction.Clear);
            await UniTask.WaitUntil(() => !_host.Busy);
            Assert.IsNull(_host.LastError);
            Assert.IsTrue(previous.IsDisposed);
        });

        [UnityTest]
        public IEnumerator SingleSceneChangeRetainsHostCoreRootAndEventSystem() => UniTask.ToCoroutine(async () =>
        {
            var core = _host.Core; var model = _host.Model;
            var root = GameObject.Find("UnityCore UI"); var events = _host.Events;
            var oldScene = SceneManager.GetActiveScene();
            var marker = new GameObject("Old scene marker");
            SceneManager.MoveGameObjectToScene(marker, oldScene);
            await _host.ExecuteAsync(DemoAction.Scene);
            Assert.IsTrue(marker == null);
            Assert.AreEqual("Other", SceneManager.GetActiveScene().name);
            Assert.AreSame(core, _host.Core); Assert.AreSame(model, _host.Model);
            Assert.AreSame(root, GameObject.Find("UnityCore UI")); Assert.AreSame(events, _host.Events);
            Assert.IsTrue(core.IsRunning);
            await Click<HomeView>(DemoAction.Next);
        });

        private static DemoCommand Command(DemoView view, DemoAction action) => view.Commands.Single(x => x.Action == action);
        private GameObject Hit(DemoCommand command)
        {
            Canvas.ForceUpdateCanvases();
            var rect = command.Button.GetComponent<RectTransform>();
            var point = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
            var pointer = new PointerEventData(_host.Events) { position = point, button = PointerEventData.InputButton.Left };
            var hits = new List<RaycastResult>(); _host.Events.RaycastAll(pointer, hits);
            Assert.IsNotEmpty(hits);
            return hits[0].gameObject;
        }
        private async UniTask Click<T>(DemoAction action) where T : DemoView
        {
            Assert.IsTrue(_host.UI.TryGet<T>(out var view, out _));
            var command = Command(view, action);
            Assert.AreEqual(command.Button.gameObject, Hit(command), "Raycast must hit the intended live button.");
            var pointer = new PointerEventData(_host.Events) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(command.Button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            await UniTask.WaitUntil(() => !_host.Busy);
            Assert.IsNull(_host.LastError);
        }
    }
}
