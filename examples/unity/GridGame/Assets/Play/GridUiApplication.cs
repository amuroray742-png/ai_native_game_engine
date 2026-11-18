#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets;
using AiNative.UnityCore.UI;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AiNative.Examples.GridGame.Play
{
    // Explicit application composition, shared across scene reloads; no static service instance.
    public sealed class GridUiApplication : MonoBehaviour
    {
        internal const string HudAddress = "Assets/UI/GridHud";
        internal AiNative.UnityCore.Core Core { get; private set; } = null!;
        internal UIModule UI { get; private set; } = null!;
        internal AssetsModule Assets { get; private set; } = null!;
        internal EventSystem Events { get; private set; } = null!;
        internal bool Idle => _operation.IsCompleted;
        internal Exception? LastError { get; private set; }
        private AssetScope? _boardScope;
        private Task _operation = Task.CompletedTask;
        private GridHudModel? _current;
        private GridHudModel _presenterModel = null!;
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();

        internal static GridUiApplication Create()
        {
            var root = new GameObject("Grid UI Application");
            root.SetActive(false);
            try
            {
                var app = root.AddComponent<GridUiApplication>();
                app.Initialize();
                DontDestroyOnLoad(root);
                root.SetActive(true);
                return app;
            }
            catch
            {
                Destroy(root);
                throw;
            }
        }

        private void Initialize()
        {
            var input = new GameObject("Grid UI EventSystem");
            input.transform.SetParent(transform, false);
            Events = input.AddComponent<EventSystem>();
            input.AddComponent<StandaloneInputModule>();
            var core = AiNative.UnityCore.Core.Inst;
            Assets = new AssetsModule();
            UI = new UIModule(new UIOptions(closedCacheCapacity: 0, referenceResolution: new Vector2(900, 650)), Events);
            UI.Register(new UIWindowDefinition<GridHud>(HudAddress, UIRole.Hud,
                (view, assets) => new GridHudPresenter(view, _presenterModel)));
            core.RegisterModule(Assets).RegisterModule(UI);
            Core = core;
            _operation = StartAsync().AsTask();
        }

        private async UniTask StartAsync()
        {
            try { await Core.RunAsync(); }
            catch (Exception error) { LastError = error; Debug.LogException(error); }
        }

        internal void Attach(GridHudModel model, string boardAddress, Action<GridBoardArt> startMatch)
        {
            if (_current != null) { throw new InvalidOperationException("A GridGame scene is already connected."); }
            _current = model;
            _operation = OpenAsync(_operation, model, boardAddress, startMatch).AsTask();
        }

        private async UniTask OpenAsync(Task previous, GridHudModel model, string boardAddress, Action<GridBoardArt> startMatch)
        {
            await previous;
            if (!model.Connected) { return; }
            try
            {
                if (LastError != null) { throw new InvalidOperationException("Previous UI operation failed.", LastError); }
                _presenterModel = model;
                var opened = await UI.OpenAsync<GridHud>(model.Token);
                if (!model.Connected) { return; }
                if (!opened.Succeeded) { throw new InvalidOperationException("Grid HUD open rejected: " + opened.Error); }
                _boardScope = Assets.CreateScope("Grid board");
                var board = await _boardScope.LoadAsync<GridBoardArt>(boardAddress, model.Token);
                if (!model.Connected) { return; }
                board.Value.Validate();
                startMatch(board.Value);
                model.MarkReady();
            }
            catch (OperationCanceledException) when (!model.Connected) { }
            catch (Exception error)
            {
                if (model.Connected)
                {
                    model.Error = error;
                    model.ShowFailure(error.Message);
                }
                Debug.LogException(error);
                await ClearAsync(Task.CompletedTask);
            }
        }

        internal void Detach(GridHudModel model)
        {
            if (!ReferenceEquals(_current, model)) { return; }
            // Revoke scene commands before PlaySceneHost releases its match synchronously.
            model.Dispose();
            _current = null;
            _operation = ClearAsync(_operation).AsTask();
        }

        private async UniTask ClearAsync(Task previous)
        {
            await previous;
            try
            {
                var result = await UI.ClearAsync();
                if (!result.Succeeded && Core.IsRunning)
                { throw new InvalidOperationException("Grid HUD clear rejected: " + result.Error); }
                _presenterModel = null!;
            }
            catch (Exception error) { LastError = error; Debug.LogException(error); }
            finally
            {
                if (_boardScope != null)
                {
                    try { await _boardScope.CloseAsync(); }
                    catch (Exception error) { LastError = error; Debug.LogException(error); }
                    _boardScope = null;
                }
            }
        }

        internal bool BlocksPointer(Vector2 screen)
        {
            _hits.Clear();
            Events.RaycastAll(new PointerEventData(Events) { position = screen }, _hits);
            return _hits.Count != 0;
        }

        private void OnDestroy()
        {
            _current?.Dispose();
            if (Core != null) { Core.CloseAsync().Forget(Debug.LogException); }
        }
    }
}
