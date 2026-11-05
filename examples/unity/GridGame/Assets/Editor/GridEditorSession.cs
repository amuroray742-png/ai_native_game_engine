#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Hosting;
using UnityEngine;

namespace AiNative.Examples.GridGame.Editor
{
    internal sealed class GridEditorSession : IDisposable
    {
        internal const ulong ExampleSeed = 42;
        private double _animationStart;
        private int? _selected;

        internal GridEditorSession(string sceneFile, ulong seed)
        {
            Module = new GridGameModule();
            var registry = new GameRegistry(Module);
            Configuration = registry.ReadScenario(JsonData.Parse(File.ReadAllBytes(sceneFile))).Configuration;
            Game = Module.CreateWithPresentation(Configuration, seed, out var frames);
            Frames = frames;
        }

        internal static string ScenarioPath => Path.Combine(Application.streamingAssetsPath, "grid.scenario.json");
        internal GridGameModule Module { get; }
        internal JsonElement Configuration { get; }
        internal IHostedGame Game { get; }
        internal SequentialPresentationQueue<GridGameView> Frames { get; }
        internal TickResult<GridGameView>? Active { get; private set; }
        internal float Progress { get; private set; }
        internal int? Selected => _selected;
        internal string Feedback { get; private set; } = "Select a unit on the active side.";

        // Editor time controls only the view. Tick advancement always requires an explicit input or step.
        internal void Present(double now, double secondsPerEvent)
        {
            if (Active == null)
            {
                Active = Frames.BeginNext();
                _animationStart = now;
            }
            if (Active == null || !Frames.IsActive(Active))
            {
                return;
            }
            double duration = secondsPerEvent * Math.Max(1, Active.Events.Count);
            Progress = duration <= 0 ? 1 : Mathf.Clamp01((float)((now - _animationStart) / duration));
            if (Progress < 1)
            {
                return;
            }
            var rejected = Active.Inputs.FirstOrDefault(input => !input.Result.Succeeded);
            Feedback = rejected?.Result.Error ?? string.Join(" · ", Active.Events.Select(item => item.GetType().Name));
            Frames.Complete(Active);
            Active = null;
        }

        internal void ClickCell(GridPosition position)
        {
            if (Frames.PendingCount != 0)
            {
                return;
            }
            GridGameView view = Frames.Presented.State;
            UnitDefinition? target = view.Units.FirstOrDefault(unit => unit.Position.Equals(position) && unit.Health > 0);
            if (target != null && target.Side == view.ActiveSide)
            {
                _selected = target.Id;
                Feedback = "Click an adjacent cell to move, or an enemy to attack.";
                return;
            }
            UnitDefinition? selected = view.Units.FirstOrDefault(unit => unit.Id == _selected && unit.Side == view.ActiveSide);
            if (selected == null)
            {
                Feedback = "Select a unit on the active side.";
                return;
            }
            if (target != null)
            {
                Submit("Attack", selected.Id, target.Id, null);
                return;
            }
            var delta = (position.X - selected.Position.X, position.Y - selected.Position.Y);
            MoveDirection? direction = delta switch
            {
                (1, 0) => MoveDirection.Right,
                (-1, 0) => MoveDirection.Left,
                (0, 1) => MoveDirection.Up,
                (0, -1) => MoveDirection.Down,
                _ => null
            };
            if (!direction.HasValue)
            {
                Feedback = "Click an adjacent cell to choose a direction.";
                return;
            }
            Submit("Move", selected.Id, null, direction);
        }

        internal void EndTurn()
        {
            if (Frames.PendingCount == 0)
            {
                Submit("EndTurn", null, null, null);
                _selected = null;
            }
        }

        private void Submit(string type, int? unit, int? target, MoveDirection? direction)
        {
            JsonElement command = JsonData.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", type);
                writer.WriteString("side", Frames.Presented.State.ActiveSide.ToString());
                if (unit.HasValue)
                {
                    writer.WriteNumber("unitId", unit.Value);
                }
                if (target.HasValue)
                {
                    writer.WriteNumber("targetId", target.Value);
                }
                if (direction.HasValue)
                {
                    writer.WriteString("direction", direction.Value.ToString());
                }
                writer.WriteEndObject();
            });
            Game.Submit(command);
            Game.Advance();
        }

        internal Vector2 DrawPosition(UnitDefinition unit, out bool hit)
        {
            var result = new Vector2(unit.Position.X, unit.Position.Y);
            hit = false;
            if (Active == null || !Frames.IsActive(Active))
            {
                return result;
            }
            float cursor = Progress * Active.Events.Count;
            for (int index = 0; index < Active.Events.Count && index <= cursor; index++)
            {
                GameEvent item = Active.Events[index];
                if (item is UnitMoved move && move.UnitId == unit.Id)
                {
                    result = Vector2.Lerp(new Vector2(move.From.X, move.From.Y),
                        new Vector2(move.To.X, move.To.Y), Mathf.Clamp01(cursor - index));
                }
                if (item is UnitDamaged damaged && damaged.UnitId == unit.Id && cursor < index + 1)
                {
                    hit = true;
                }
            }
            return result;
        }

        public void Dispose()
        {
            Frames.Reset(Frames.Presented);
            Active = null;
            Game.Dispose();
        }
    }
}
