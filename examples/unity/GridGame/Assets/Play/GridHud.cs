#nullable enable
using System.Linq;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.UnityCore.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridHud : UIView
    {
        [SerializeField] private Text turn = null!;
        [SerializeField] private Text counters = null!;
        [SerializeField] private Text selection = null!;
        [SerializeField] private Button endTurn = null!;
        [SerializeField] private Button restart = null!;
        internal Button EndTurn => endTurn;
        internal Button Restart => restart;

        internal void Show(Observation<GridGameView> frame, int? id, string feedback, bool settled)
        {
            var view = frame.State;
            turn.text = view.Winner.HasValue ? $"SIDE {view.Winner} WINS" : $"SIDE {view.ActiveSide}   /   TURN {view.TurnIndex}";
            counters.text = $"{view.ActionPoints} AP     ·     TICK {frame.Tick}";
            var unit = view.Units.FirstOrDefault(item => item.Id == id);
            selection.text = unit == null ? feedback : $"{unit.Side}{unit.Id}    {unit.Health} HP     {feedback}";
            endTurn.interactable = settled && !view.Winner.HasValue;
        }

        internal void SetConnected(bool connected)
        {
            restart.interactable = connected;
            if (!connected) { endTurn.interactable = false; }
        }

        internal void ShowFailure(string message) => selection.text = message;
    }
}
