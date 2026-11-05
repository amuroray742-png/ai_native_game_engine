#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiNative.Core;
using AiNative.GridGame;
using AiNative.GridGame.Hosting;
using AiNative.Hosting;
using AiNative.Unity;
using UnityEngine;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridGameBootstrap : PlaySceneHost<GridCommand, GridGameView>
    {
        [SerializeField] private Camera boardCamera = null!;
        [SerializeField] private GridBoard board = null!;
        [SerializeField] private GridHud hud = null!;
        [SerializeField, Min(0)] private float secondsPerEvent = 0.18f;

        private TickResult<GridGameView>? _active;
        private GridBoard? _display;
        private double _start;
        private int? _selected;
        private string _feedback = "";
        internal GridGameModule Module { get; } = new GridGameModule();
        internal GridHost Session { get; private set; } = null!;
        internal JsonElement Configuration { get; private set; }
        internal SequentialPresentationQueue<GridGameView> Frames => Host.Sequential!;
        internal float SecondsPerEvent { get => secondsPerEvent; set => secondsPerEvent = value; }
        internal int? Selected => _selected;

        protected override void ConnectScene()
        {
            hud.EndTurn.onClick.AddListener(EndTurn);
            hud.Restart.onClick.AddListener(Restart);
            Restart();
        }

        public void Restart()
        {
            try
            {
                string file = Path.Combine(Application.streamingAssetsPath, "grid.scenario.json");
                var config = new GameRegistry(Module).ReadScenario(JsonData.Parse(File.ReadAllBytes(file))).Configuration;
                ReplaceMatch(config);
            }
            catch (Exception error)
            {
                _feedback = error.Message;
                hud.ShowFailure(_feedback);
                Debug.LogException(error, this);
            }
        }

        internal void ReplaceMatch(JsonElement config)
        {
            GridHost? candidate = null;
            GridBoard? staged = null;
            try
            {
                Host.Replace(() =>
                {
                    candidate = Module.CreatePlay(config, 42);
                    try
                    {
                        staged = board.Stage(candidate.CaptureObservation().State, boardCamera);
                        return candidate;
                    }
                    catch
                    {
                        candidate.Dispose();
                        throw;
                    }
                });
            }
            catch
            {
                if (staged != null)
                {
                    staged.Retire();
                }
                if (Host.IsFaulted && Host.Sequential == null)
                {
                    RemoveDisplay();
                    _active = null;
                    Session = null!;
                }
                throw;
            }
            RemoveDisplay();
            _display = staged!;
            _display.gameObject.SetActive(true);
            Session = candidate!;
            Configuration = config;
            _selected = null;
            _active = null;
            _feedback = "Select your unit";
            Draw(0);
        }

        private void RemoveDisplay()
        {
            if (_display != null)
            {
                _display.Retire();
                _display = null;
            }
        }

        protected override void DisconnectScene()
        {
            hud.EndTurn.onClick.RemoveListener(EndTurn);
            hud.Restart.onClick.RemoveListener(Restart);
            RemoveDisplay();
            _active = null;
            Session = null!;
        }

        protected override void SampleInput()
        {
            if (Input.GetMouseButtonDown(0))
            {
                HandlePointer(Input.mousePosition);
            }
        }

        protected override void PresentFrame()
        {
            if (_active == null)
            {
                _active = Frames.BeginNext();
                _start = Time.timeAsDouble;
            }
            float progress = 0;
            if (_active != null && Frames.IsActive(_active))
            {
                double duration = secondsPerEvent * Math.Max(1, _active.Events.Count);
                progress = duration <= 0 ? 1 : Mathf.Clamp01((float)((Time.timeAsDouble - _start) / duration));
                if (progress >= 1)
                {
                    var rejection = _active.Inputs.FirstOrDefault(input => !input.Result.Succeeded);
                    _feedback = rejection?.Result.Error ?? "";
                    Frames.Complete(_active);
                    _active = null;
                }
            }
            Draw(progress);
        }

        private void Draw(float progress)
        {
            _display!.Fit(Frames.Presented.State, boardCamera);
            _display.Draw(Frames.Presented.State, _selected, _active, progress);
            hud.Show(Frames.Presented, _selected, _feedback, Frames.PendingCount == 0);
        }

        internal void HandlePointer(Vector2 screen)
        {
            if (hud.BlocksPointer(screen) || Frames.PendingCount != 0 || Frames.Presented.State.Winner.HasValue)
            {
                return;
            }
            Vector3 world = boardCamera.ScreenToWorldPoint(screen);
            Vector3 local = board.transform.InverseTransformPoint(world);
            GridPosition cell = new GridPosition(Mathf.FloorToInt(local.x + 0.5f), Mathf.FloorToInt(local.y + 0.5f));
            var view = Frames.Presented.State;
            if (cell.X < 0 || cell.Y < 0 || cell.X >= view.Width || cell.Y >= view.Height)
            {
                return;
            }
            var target = view.Units.FirstOrDefault(u => u.Position.Equals(cell) && u.Health > 0);
            if (target != null && target.Side == view.ActiveSide)
            {
                _selected = target.Id;
                _feedback = "Choose an adjacent tile or an enemy";
                return;
            }
            var unit = view.Units.FirstOrDefault(u => u.Id == _selected && u.Side == view.ActiveSide);
            if (unit == null)
            {
                return;
            }
            if (target != null)
            {
                Act(new Attack(view.ActiveSide, unit.Id, target.Id));
                return;
            }
            MoveDirection? direction = (cell.X - unit.Position.X, cell.Y - unit.Position.Y) switch
            {
                (1, 0) => MoveDirection.Right, (-1, 0) => MoveDirection.Left,
                (0, 1) => MoveDirection.Up, (0, -1) => MoveDirection.Down, _ => null
            };
            if (direction.HasValue)
            {
                Act(new Move(view.ActiveSide, unit.Id, direction.Value));
            }
        }

        private void EndTurn()
        {
            if (Session != null && Frames.PendingCount == 0 && !Frames.Presented.State.Winner.HasValue)
            {
                Act(new EndTurn(Frames.Presented.State.ActiveSide));
                _selected = null;
            }
        }

        private void Act(GridCommand command)
        {
            Submit(command);
            Advance();
        }

        internal long Submit(GridCommand command) => Host.Submit(command);
        internal TickResult<GridGameView> Advance() => Host.AdvanceManual();
    }
}
