#nullable enable
using System.Collections.Generic;
using AiNative.Core;
using AiNative.GridGame;
using UnityEngine;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridBoard : MonoBehaviour
    {
        private GridBoardArt _art = null!;
        private static readonly Color Mint = new Color32(103, 227, 194, 255);
        private static readonly Color Coral = new Color32(255, 145, 112, 255);
        private readonly Dictionary<int, SpriteRenderer> _pieces = new Dictionary<int, SpriteRenderer>();
        // Build and validate a hidden candidate under the scene's board root. The live board and camera
        // are untouched until Bootstrap publishes this display with its new session.
        internal GridBoard Stage(GridGameView view, Camera camera, GridBoardArt art)
        {
            // Each tile is a scene object. Keep the sample's display budget separate from rule validation.
            if ((long)view.Width * view.Height > 4096)
            {
                throw new System.InvalidOperationException("GridGame Play can display up to 4096 tiles.");
            }
            var root = new GameObject("Match pieces");
            root.SetActive(false);
            root.transform.SetParent(transform, false);
            var next = root.AddComponent<GridBoard>();
            next._art = art;
            try
            {
                next.Build(view);
                next.Draw(view, null, null, 0);
                if (camera == null || !camera.orthographic)
                {
                    throw new System.InvalidOperationException("GridGame requires an orthographic board camera.");
                }
                return next;
            }
            catch
            {
                next.Retire();
                throw;
            }
        }

        private void Build(GridGameView view)
        {
            for (int y = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++)
                {
                    var tile = Add($"Tile {x},{y}", _art.Square, transform, new Vector2(x, y), .96f, 0);
                    tile.color = (x + y) % 2 == 0 ? new Color32(49, 64, 81, 255) : new Color32(57, 75, 90, 255);
                }
            }
            foreach (var wall in view.Obstacles)
            {
                var tile = Add("Wall", _art.Square, transform, new Vector2(wall.X, wall.Y), .78f, 1);
                tile.color = new Color32(23, 32, 47, 255);
            }
            foreach (var unit in view.Units)
            {
                var piece = Add($"{unit.Side}{unit.Id}", unit.Side == Side.A ? _art.Tower : _art.Diamond, transform,
                    new Vector2(unit.Position.X, unit.Position.Y), .84f, 3);
                _pieces.Add(unit.Id, piece);
            }
        }

        private SpriteRenderer Add(string name, Sprite sprite, Transform parent, Vector2 pos, float size, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * size;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sharedMaterial = _art.Material;
            renderer.sprite = sprite;
            renderer.sortingOrder = order;
            return renderer;
        }

        internal void Fit(GridGameView view, Camera camera)
        {
            Vector3 center = transform.TransformPoint(new Vector3((view.Width - 1) * .5f, (view.Height - 1) * .5f));
            camera.transform.position = new Vector3(center.x, center.y, -10);
            camera.orthographicSize = Mathf.Max((view.Height + .6f) / 1.4f, (view.Width + 1f) / (2 * camera.aspect));
        }

        internal void Draw(GridGameView view, int? selected, TickResult<GridGameView>? frame, float progress)
        {
            foreach (var unit in view.Units)
            {
                SpriteRenderer piece = _pieces[unit.Id];
                piece.enabled = unit.Health > 0;
                var pos = new Vector2(unit.Position.X, unit.Position.Y);
                bool hit = false;
                if (frame != null)
                {
                    float cursor = progress * frame.Events.Count;
                    for (int i = 0; i < frame.Events.Count && i <= cursor; i++)
                    {
                        if (frame.Events[i] is UnitMoved move && move.UnitId == unit.Id)
                        {
                            pos = Vector2.Lerp(new Vector2(move.From.X, move.From.Y), new Vector2(move.To.X, move.To.Y), cursor - i);
                        }
                        if (frame.Events[i] is UnitDamaged damage && damage.UnitId == unit.Id && cursor < i + 1)
                        {
                            hit = true;
                        }
                    }
                }
                piece.transform.localPosition = pos;
                piece.transform.localScale = Vector3.one * (selected == unit.Id ? .95f : .80f);
                piece.color = hit ? Color.white : unit.Side == Side.A ? Mint : Coral;
            }
        }

        internal void Retire()
        {
            gameObject.SetActive(false);
            Destroy(gameObject);
        }
    }
}
