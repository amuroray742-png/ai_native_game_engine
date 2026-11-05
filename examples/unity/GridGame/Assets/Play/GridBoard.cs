#nullable enable
using System.Collections.Generic;
using AiNative.Core;
using AiNative.GridGame;
using UnityEngine;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridBoard : MonoBehaviour
    {
        private static readonly Color Mint = new Color32(103, 227, 194, 255);
        private static readonly Color Coral = new Color32(255, 145, 112, 255);
        private readonly Dictionary<int, SpriteRenderer> _pieces = new Dictionary<int, SpriteRenderer>();
        private Sprite _square = null!;
        private Sprite _tower = null!;
        private Sprite _diamond = null!;
        private Texture2D _icons = null!;

        // Build and validate a hidden candidate under the scene's board root. The live board and camera
        // are untouched until Bootstrap publishes this display with its new session.
        internal GridBoard Stage(GridGameView view, Camera camera)
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
            var white = Texture2D.whiteTexture;
            _square = Sprite.Create(white, new Rect(0, 0, white.width, white.height), Vector2.one * .5f, white.width);
            CreateIcons();
            for (int y = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++)
                {
                    var tile = Add($"Tile {x},{y}", _square, transform, new Vector2(x, y), .96f, 0);
                    tile.color = (x + y) % 2 == 0 ? new Color32(49, 64, 81, 255) : new Color32(57, 75, 90, 255);
                }
            }
            foreach (var wall in view.Obstacles)
            {
                var tile = Add("Wall", _square, transform, new Vector2(wall.X, wall.Y), .78f, 1);
                tile.color = new Color32(23, 32, 47, 255);
            }
            foreach (var unit in view.Units)
            {
                var piece = Add($"{unit.Side}{unit.Id}", unit.Side == Side.A ? _tower : _diamond, transform,
                    new Vector2(unit.Position.X, unit.Position.Y), .84f, 3);
                _pieces.Add(unit.Id, piece);
            }
        }

        private static SpriteRenderer Add(string name, Sprite sprite, Transform parent, Vector2 pos, float size, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * size;
            var renderer = go.AddComponent<SpriteRenderer>();
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

        // Two small monochrome faction emblems, independent of fonts and editor assets.
        private void CreateIcons()
        {
            _icons = new Texture2D(32, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var pixels = new Color32[32 * 16];
            for (int y = 1; y < 15; y++)
            {
                for (int x = 1; x < 15; x++)
                {
                    bool tower = y <= 3 || (y < 11 && x >= 4 && x <= 11) || (y >= 10 && (y < 12 || x <= 3 || x >= 12 || (x >= 6 && x <= 9)));
                    if (tower)
                    {
                        pixels[y * 32 + x] = Color.white;
                    }
                    float distance = Mathf.Abs(x - 7.5f) + Mathf.Abs(y - 7.5f);
                    if (distance <= 7.5f && distance >= 2)
                    {
                        pixels[y * 32 + 16 + x] = Color.white;
                    }
                }
            }
            _icons.SetPixels32(pixels);
            _icons.Apply();
            _tower = Sprite.Create(_icons, new Rect(0, 0, 16, 16), Vector2.one * .5f, 16);
            _diamond = Sprite.Create(_icons, new Rect(16, 0, 16, 16), Vector2.one * .5f, 16);
        }

        internal void Retire()
        {
            gameObject.SetActive(false);
            // Unity may omit OnDestroy on a never-activated candidate.
            ReleaseGraphics();
            Destroy(gameObject);
        }

        private void OnDestroy() => ReleaseGraphics();

        private void ReleaseGraphics()
        {
            Destroy(_square);
            Destroy(_tower);
            Destroy(_diamond);
            Destroy(_icons);
            _square = null!;
            _tower = null!;
            _diamond = null!;
            _icons = null!;
        }
    }
}
