#nullable enable
using System;
using AiNative.GridGame;
using UnityEditor;
using UnityEngine;

namespace AiNative.Examples.UnityEditor
{
    public sealed class GridGameWindow : EditorWindow
    {
        private GridEditorSession? _session;
        private string? _failure;
        private float _secondsPerEvent = 0.25f;

        [MenuItem("Window/AI Native/GridGame")]
        public static void Open()
        {
            var window = GetWindow<GridGameWindow>("GridGame");
            window.minSize = new Vector2(360, 400);
            window.Show();
        }

        private void OnEnable() => Restart();

        private void Restart()
        {
            try
            {
                var candidate = new GridEditorSession(GridEditorSession.ExampleFile("grid.scenario.json"), GridEditorSession.ExampleSeed);
                _session?.Dispose();
                _session = candidate;
                _failure = null;
            }
            catch (Exception error)
            {
                _failure = error.Message;
                Debug.LogException(error);
            }
        }

        private void OnDisable()
        {
            _session?.Dispose();
            _session = null;
        }

        private void Update()
        {
            if (_session != null && _session.Frames.PendingCount > 0)
            {
                _session.Present(EditorApplication.timeSinceStartup, _secondsPerEvent);
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (GUILayout.Button("New match", GUILayout.Width(100)))
            {
                Restart();
            }
            if (_failure != null)
            {
                EditorGUILayout.HelpBox(_failure, MessageType.Error);
            }
            if (_session == null)
            {
                return;
            }
            GridGameView view = _session.Frames.Presented.State;
            EditorGUILayout.LabelField(view.Winner.HasValue ? $"{view.Winner} wins" : $"Side {view.ActiveSide}   ·   {view.ActionPoints} AP",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Turn {view.TurnIndex}   ·   Tick {_session.Frames.Presented.Tick}");
            _secondsPerEvent = EditorGUILayout.Slider("Seconds per event", _secondsPerEvent, 0, 1);
            float cell = Mathf.Min((position.width - 16) / view.Width, (position.height - 170) / view.Height, 64);
            Rect board = GUILayoutUtility.GetRect(cell * view.Width, cell * view.Height, GUILayout.ExpandWidth(false));
            DrawBoard(_session, board, cell);
            using (new EditorGUI.DisabledScope(_session.Frames.PendingCount != 0 || view.Winner.HasValue))
            {
                if (GUILayout.Button("End turn", GUILayout.Width(100)))
                {
                    _session.EndTurn();
                }
            }
            EditorGUILayout.LabelField(_session.Feedback, EditorStyles.wordWrappedLabel);
        }

        private static void DrawBoard(GridEditorSession session, Rect board, float cell)
        {
            GridGameView view = session.Frames.Presented.State;
            Rect Cell(float x, float y) => new Rect(board.x + x * cell, board.y + (view.Height - 1 - y) * cell, cell - 1, cell - 1);
            for (int y = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++)
                {
                    EditorGUI.DrawRect(Cell(x, y), (x + y) % 2 == 0 ? new Color(0.20f, 0.24f, 0.28f) : new Color(0.24f, 0.28f, 0.32f));
                }
            }
            foreach (GridPosition obstacle in view.Obstacles)
            {
                EditorGUI.DrawRect(Cell(obstacle.X, obstacle.Y), Color.black);
            }
            foreach (UnitDefinition unit in view.Units)
            {
                if (unit.Health == 0)
                {
                    continue;
                }
                Vector2 pos = session.DrawPosition(unit, out bool hit);
                Rect tile = Cell(pos.x, pos.y);
                if (session.Selected == unit.Id)
                {
                    EditorGUI.DrawRect(tile, Color.white);
                }
                tile = new Rect(tile.x + 3, tile.y + 3, tile.width - 6, tile.height - 6);
                Color side = unit.Side == Side.A ? new Color(0.13f, 0.46f, 0.73f) : new Color(0.67f, 0.28f, 0.22f);
                EditorGUI.DrawRect(tile, hit ? Color.yellow : side);
                GUI.Label(tile, $" {unit.Side}{unit.Id}\n {unit.Health} HP", EditorStyles.whiteLabel);
            }
            Event input = Event.current;
            if (input.type == EventType.MouseDown && input.button == 0 && board.Contains(input.mousePosition) && !view.Winner.HasValue)
            {
                int x = Mathf.FloorToInt((input.mousePosition.x - board.x) / cell);
                int y = view.Height - 1 - Mathf.FloorToInt((input.mousePosition.y - board.y) / cell);
                session.ClickCell(new GridPosition(x, y));
                input.Use();
            }
        }
    }
}
