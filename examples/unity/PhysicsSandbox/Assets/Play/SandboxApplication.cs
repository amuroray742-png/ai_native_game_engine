#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using AppCore = AiNative.UnityCore.Core;

namespace AiNative.Examples.PhysicsPlay
{
    public sealed class SandboxApplication : MonoBehaviour
    {
        private readonly AssetsModule _assets = new AssetsModule();
        private UIModule _ui = null!;
        private AppCore _core = null!;
        private SandboxScene? _owner;
        private SandboxScene _presenterScene = null!;
        private CancellationToken _presenterToken;
        private AssetScope? _bodyScope;
        private CancellationTokenSource? _lifetime;
        private Task _operation = Task.CompletedTask;
        internal bool Idle => _operation.IsCompleted;
        internal AssetDiagnostics Diagnostics => _assets.GetDiagnostics();

        internal void Initialize()
        {
            DontDestroyOnLoad(gameObject);
            var input = new GameObject("Sandbox EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            input.transform.SetParent(transform, false);
            _ui = new UIModule(new UIOptions(closedCacheCapacity: 0, referenceResolution: new Vector2(1000, 510),
                matchWidthOrHeight: .55f), input.GetComponent<EventSystem>());
            _ui.Register(new UIWindowDefinition<SandboxHud>("Assets/Presentation/SandboxHud", UIRole.Hud,
                (view, _) => new SandboxHudPresenter(view, _presenterScene, _presenterToken)));
            _core = AppCore.Inst;
            _core.RegisterModule(_assets).RegisterModule(_ui);
            _operation = _core.RunAsync().AsTask();
        }

        internal void Attach(SandboxScene scene)
        {
            if (_owner != null) { throw new InvalidOperationException("Another Sandbox scene is already connected."); }
            _owner = scene;
            _lifetime = new CancellationTokenSource();
            _operation = LoadAsync(_operation, scene, _lifetime.Token).AsTask();
        }

        private async UniTask LoadAsync(Task previous, SandboxScene scene, CancellationToken token)
        {
            try
            {
                // Publish the operation before a startup error can disable and detach the scene.
                await UniTask.Yield();
                await previous;
                token.ThrowIfCancellationRequested();
                _bodyScope = _assets.CreateScope("Sandbox scene");
                var box = await _bodyScope.LoadAsync<GameObject>("Assets/Presentation/Box", token);
                var sphere = await _bodyScope.LoadAsync<GameObject>("Assets/Presentation/Sphere", token);
                _presenterScene = scene;
                _presenterToken = token;
                var opened = await _ui.OpenAsync<SandboxHud>(token);
                token.ThrowIfCancellationRequested();
                if (!opened.Succeeded) { throw new InvalidOperationException("Sandbox HUD open rejected: " + opened.Error); }
                scene.StartWorld(box.Value, sphere.Value);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (token.IsCancellationRequested) { Debug.LogException(error); }
                else { scene.ReportStartupFailure(error); }
            }
        }

        internal void Detach(SandboxScene scene)
        {
            if (!ReferenceEquals(scene, _owner)) { return; }
            _owner = null;
            var lifetime = _lifetime!;
            _lifetime = null;
            lifetime.Cancel();
            _operation = ClearAsync(_operation, lifetime).AsTask();
            _operation.AsUniTask().Forget(Debug.LogException);
        }

        private async UniTask ClearAsync(Task previous, CancellationTokenSource lifetime)
        {
            try
            {
                await previous;
                if (_core.IsRunning)
                {
                    var cleared = await _ui.ClearAsync();
                    if (!cleared.Succeeded) { throw new InvalidOperationException("Sandbox HUD clear rejected: " + cleared.Error); }
                }
            }
            finally
            {
                lifetime.Dispose();
                if (_bodyScope != null) { await _bodyScope.CloseAsync(); }
                _bodyScope = null;
                _presenterScene = null!;
            }
        }

        private void OnDestroy()
        {
            if (_core != null) { _core.CloseAsync().Forget(Debug.LogException); }
        }
    }
}
