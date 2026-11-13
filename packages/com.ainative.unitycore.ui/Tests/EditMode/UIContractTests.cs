using System;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.UnityCore.UI.Tests
{
    public sealed class UIContractTests
    {
        [TestCase(-1, 16)]
        [TestCase(8, 0)]
        public void InvalidCapacityIsRejected(int capacity, int depth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UIOptions(capacity, depth));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-1f)]
        public void InvalidTransitionDurationIsRejected(float duration)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UITransitionOptions(duration, true, Vector2.zero, 1));
        }

        [Test]
        public void InvalidScaleAndOffsetAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UITransitionOptions(1, true, Vector2.zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new UITransitionOptions(1, true, new Vector2(float.NaN, 0), 1));
        }

        [TestCase(UIRole.Hud)]
        [TestCase(UIRole.Modal)]
        [TestCase(UIRole.Overlay)]
        public void NonPageCannotDeclarePagePolicy(UIRole role)
        {
            Assert.Throws<ArgumentException>(() => new UIWindowDefinition<ContractView>(
                "UI/Test", role, (view, assets) => new ContractPresenter(), opaqueFullscreen: true));
            Assert.Throws<ArgumentException>(() => new UIWindowDefinition<ContractView>(
                "UI/Test", role, (view, assets) => new ContractPresenter(), allowHudInput: true));
        }

        [TestCase(UIRole.Hud)]
        [TestCase(UIRole.Page)]
        [TestCase(UIRole.Modal)]
        [TestCase(UIRole.Overlay)]
        public void DefinitionRetainsItsFixedRole(UIRole role)
        {
            var definition = new UIWindowDefinition<ContractView>("UI/Test", role,
                (view, assets) => new ContractPresenter());
            Assert.AreEqual(role, definition.Role);
            Assert.IsFalse(definition.AllowHudInput);
            Assert.IsFalse(definition.OpaqueFullscreen);
        }

        [Test]
        public void RejectedOpenCannotExposeSuccessfulPayload()
        {
            var rejected = UIOpenResult<ContractView>.Rejected(UIError.NotRunning);
            Assert.IsFalse(rejected.Succeeded);
            Assert.AreEqual(UIError.NotRunning, rejected.Error);
            Assert.Throws<InvalidOperationException>(() => { var view = rejected.View; });
            Assert.Throws<InvalidOperationException>(() => { var handle = rejected.Handle; });
        }

        [Test]
        public void DefaultResultCannotMasqueradeAsSuccess()
        {
            Assert.IsFalse(default(UIOpenResult<ContractView>).Succeeded);
            Assert.IsFalse(default(UIResult).Succeeded);
        }

        [Test]
        public void DuplicateWindowDefinitionIsRejected()
        {
            var host = new GameObject("Contract EventSystem", typeof(UnityEngine.EventSystems.EventSystem));
            try
            {
                var module = new UIModule(new UIOptions(), host.GetComponent<UnityEngine.EventSystems.EventSystem>());
                var definition = new UIWindowDefinition<ContractView>("UI/Test", UIRole.Hud,
                    (view, assets) => new ContractPresenter());
                module.Register(definition);
                Assert.Throws<InvalidOperationException>(() => module.Register(definition));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void StoppedModuleRejectsOperationsWithoutCreatingWindows()
        {
            var host = new GameObject("Stopped EventSystem", typeof(UnityEngine.EventSystems.EventSystem));
            try
            {
                IUI ui = new UIModule(new UIOptions(), host.GetComponent<UnityEngine.EventSystems.EventSystem>());
                Assert.AreEqual(UIError.NotRunning, ui.OpenAsync<ContractView>().GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.PushAsync<ContractView>().GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.CloseAsync(default).GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.ReleaseAsync(default).GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.BackAsync().GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.TrimAsync().GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.ClearAsync().GetAwaiter().GetResult().Error);
                Assert.AreEqual(UIError.NotRunning, ui.BringToFront(default).Error);
                Assert.IsFalse(ui.TryGet<ContractView>(out var view, out var handle));
                Assert.IsNull(view);
                Assert.AreEqual(default(UIHandle), handle);
                Assert.IsFalse(ui.HasModal);
                var diagnostics = ui.GetDiagnostics();
                Assert.AreEqual(0, diagnostics.WindowCount);
                Assert.AreEqual(0, diagnostics.PendingOperations);
                Assert.AreEqual(0, diagnostics.PageDepth);
                Assert.AreEqual(0, diagnostics.ModalCount);
                Assert.AreEqual(0, diagnostics.ClosedCacheCount);
                Assert.AreEqual(0, diagnostics.FailedCleanupCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void OperationFaultRetainsCauseAndCommitState()
        {
            var cause = new InvalidOperationException("Presenter failed");
            var fault = new UIOperationException("Open failed", cause, true);
            Assert.AreSame(cause, fault.InnerException);
            Assert.IsTrue(fault.Committed);
            Assert.IsFalse(fault.Handle.HasValue);
            Assert.IsFalse(fault.CleanupHandle.HasValue);
        }

        private sealed class ContractPresenter : UIPresenter
        {
            public override void Bind() { }
            public override void Resume() { }
            public override void Suspend() { }
            public override void Unbind() { }
        }
    }
}
