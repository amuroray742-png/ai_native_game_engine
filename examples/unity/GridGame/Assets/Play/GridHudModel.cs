#nullable enable
using System;
using System.Threading;
using AiNative.Core;
using AiNative.GridGame;

namespace AiNative.Examples.GridGame.Play
{
    // Scene-owned presentation only. Never retains a GridHost or advances the simulation.
    internal sealed class GridHudModel : IDisposable
    {
        private Action? _endTurn;
        private Action? _restart;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        internal CancellationToken Token { get; }
        internal bool Connected { get; private set; } = true;
        internal bool Ready { get; private set; }
        internal Observation<GridGameView>? Frame { get; private set; }
        internal int? Selected { get; private set; }
        internal string Feedback { get; private set; } = "";
        internal bool Settled { get; private set; }
        internal Exception? Error { get; set; }
        internal event Action? Changed;

        internal GridHudModel(Action endTurn, Action restart)
        {
            _endTurn = endTurn;
            _restart = restart;
            Token = _lifetime.Token;
        }

        internal void Show(Observation<GridGameView> frame, int? selected, string feedback, bool settled)
        {
            if (!Connected) { return; }
            Frame = frame; Selected = selected; Feedback = feedback; Settled = settled;
            Changed?.Invoke();
        }

        internal void ShowFailure(string message) { Feedback = message; Changed?.Invoke(); }
        internal void MarkReady()
        {
            if (!Connected) { return; }
            Ready = true;
            Changed?.Invoke();
        }
        internal void EndTurn() { if (Connected && Ready && Settled) { _endTurn!(); } }
        internal void Restart() { if (Connected && Ready) { _restart!(); } }

        public void Dispose()
        {
            if (!Connected) { return; }
            Connected = false; Ready = false;
            _endTurn = null; _restart = null; Frame = null;
            Changed?.Invoke();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
