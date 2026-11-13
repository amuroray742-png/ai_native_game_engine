using System;
using System.Collections.Generic;
using System.Threading;
using AiNative.UnityCore.TimeMachine;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.UnityCore.UI
{
    internal sealed partial class UIRun
    {
        internal readonly ITimeMachine Time;
        private ITimeTicket _screenTicket;
        private int _screenWidth = -1;
        private int _screenHeight = -1;
        private Rect _safeArea;
        private readonly Queue<Action> _completions = new Queue<Action>();

        internal void CompleteOutsideCommit(Action completion)
        {
            if (Committing) { _completions.Enqueue(completion); }
            else { completion(); }
        }

        private void UpdateScreen(float ignored)
        {
            ObserveDestroyedWindows();
            if (!Accepting || (_screenWidth == Screen.width && _screenHeight == Screen.height && _safeArea == Screen.safeArea)) { return; }
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            _safeArea = Screen.safeArea;
            Rect normalized = UISafeArea.Normalize(_screenWidth, _screenHeight, _safeArea);
            try { Commit(() =>
            {
                foreach (var record in new List<UIWindowRecord>(Windows.Values))
                {
                    if (!Accepting) { break; }
                    if (record.Bound && record.View != null) { UISafeArea.Apply(record.View, normalized, Lifetime.Token); }
                }
            }); }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        }

        private async UniTask PlayTransitionAsync(UIWindowRecord record, bool entering, CancellationToken token)
        {
            if (record.TransitionOptions.DurationSeconds == 0) { return; }
            record.Transitioning = true;
            try
            {
                Commit(() => record.Transition = new UITransition(this, record.View, record.TransitionOptions));
                await record.Transition.PlayAsync(entering, token);
            }
            finally { record.Transitioning = false; record.Transition = null; }
        }

        private void StopScreenUpdates()
        {
            _screenTicket?.Dispose();
            _screenTicket = null;
        }
    }
}
