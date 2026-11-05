#nullable enable
using System.Collections.Generic;
using System.Linq;
using AiNative.Core;
using AiNative.GridGame;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridHud : MonoBehaviour
    {
        [SerializeField] private Text turn = null!;
        [SerializeField] private Text counters = null!;
        [SerializeField] private Text selection = null!;
        [SerializeField] private Button endTurn = null!;
        [SerializeField] private Button restart = null!;
        [SerializeField] private GraphicRaycaster raycaster = null!;
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        internal Button EndTurn => endTurn;
        internal Button Restart => restart;

        internal bool BlocksPointer(Vector2 screen)
        {
            _hits.Clear();
            raycaster.Raycast(new PointerEventData(EventSystem.current) { position = screen }, _hits);
            return _hits.Count != 0;
        }

        internal void Show(Observation<GridGameView> frame, int? id, string feedback, bool settled)
        {
            var view = frame.State;
            turn.text = view.Winner.HasValue ? $"SIDE {view.Winner} WINS" : $"SIDE {view.ActiveSide}   /   TURN {view.TurnIndex}";
            counters.text = $"{view.ActionPoints} AP     ·     TICK {frame.Tick}";
            var unit = view.Units.FirstOrDefault(item => item.Id == id);
            selection.text = unit == null ? feedback : $"{unit.Side}{unit.Id}    {unit.Health} HP     {feedback}";
            endTurn.interactable = settled && !view.Winner.HasValue;
        }

        internal void ShowFailure(string message) => selection.text = message;
    }
}
