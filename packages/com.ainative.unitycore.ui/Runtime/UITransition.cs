using System;
using System.Threading;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.UI
{
    internal sealed class UITransition
    {
        private readonly UIRun _run;
        private readonly UIView _view;
        private readonly UITransitionOptions _options;
        private readonly CanvasGroup _visualGroup;
        private readonly Vector2 _position;
        private readonly Vector3 _scale;
        private readonly float _alpha;
        private readonly UniTaskCompletionSource _completion = new UniTaskCompletionSource();
        private ITimeTicket _ticket;
        private CancellationTokenRegistration _cancellation;
        private CancellationToken _token;
        private bool _entering;
        private bool _finished;
        private float _elapsed;

        internal UITransition(UIRun run, UIView view, UITransitionOptions options)
        {
            _run = run;
            _view = view;
            _options = options;
            _position = view.Visual.anchoredPosition;
            _scale = view.Visual.localScale;
            _visualGroup = view.Visual.GetComponent<CanvasGroup>();
            if (_visualGroup == null) { _visualGroup = view.Visual.gameObject.AddComponent<CanvasGroup>(); }
            _alpha = _visualGroup.alpha;
        }

        internal UniTask PlayAsync(bool entering, CancellationToken token)
        {
            _entering = entering;
            _token = token;
            try
            {
                _run.Commit(() => { Apply(entering ? 0 : 1); token.ThrowIfCancellationRequested(); });
                _ticket = _run.Time.RegisterUpdate(Tick, "UI.Transition");
                _cancellation = token.Register(() => Finish(new OperationCanceledException(token)));
                // Registration invokes synchronously if a callback already cancelled the token.
                if (_finished) { _cancellation.Dispose(); }
            }
            catch (Exception error) { Finish(error); }
            return _completion.Task;
        }

        private void Tick(float scaledDelta)
        {
            if (_finished) { return; }
            try
            {
                _elapsed += UnityEngine.Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(_elapsed / _options.DurationSeconds);
                _run.Commit(() =>
                {
                    _token.ThrowIfCancellationRequested();
                    Apply(_entering ? progress : 1 - progress);
                    _token.ThrowIfCancellationRequested();
                });
                if (progress >= 1) { Finish(null); }
            }
            catch (Exception error) { Finish(error); }
        }

        private void Apply(float amount)
        {
            _visualGroup.alpha = _options.Fade ? _alpha * amount : _alpha;
            _token.ThrowIfCancellationRequested();
            _view.Visual.anchoredPosition = _position + _options.EnterOffset * (1 - amount);
            _token.ThrowIfCancellationRequested();
            _view.Visual.localScale = _scale * Mathf.Lerp(_options.EnterScale, 1, amount);
            _token.ThrowIfCancellationRequested();
        }

        private void Finish(Exception error)
        {
            if (_finished) { return; }
            _finished = true;
            _ticket?.Dispose();
            _ticket = null;
            _cancellation.Dispose();
            try
            {
                _run.Commit(() =>
                {
                    if (_visualGroup != null) { _visualGroup.alpha = _alpha; }
                    var visual = _view != null ? _view.Visual : null;
                    if (visual != null) { visual.anchoredPosition = _position; }
                    if (visual != null) { visual.localScale = _scale; }
                });
            }
            catch (Exception reset) { error = error == null ? reset : new AggregateException(error, reset); }
            _run.CompleteOutsideCommit(() =>
            {
                if (error == null) { _completion.TrySetResult(); }
                else { _completion.TrySetException(error); }
            });
        }
    }
}
