#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AiNative.Core;
using AiNative.Examples.GridGame.Play;
using AiNative.GridGame;
using AiNative.Hosting;
using AiNative.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AiNative.Examples.GridGame.PlayTests
{
    public sealed class GridPlayTests
    {
        private GridGameBootstrap _game = null!;
        private string _output = "";
        private string _inputs = "";

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            Assert.IsTrue(Application.isPlaying);
            yield return SceneManager.LoadSceneAsync("GridGame");
            yield return null;
            _game = Object.FindFirstObjectByType<GridGameBootstrap>();
            Assert.NotNull(_game);
            Assert.AreEqual(0, _game.Frames.Presented.Tick);
            _output = Environment.GetEnvironmentVariable("AINATIVE_GRID_EVIDENCE") ?? Path.Combine(Application.temporaryCachePath, "grid-verification");
            _inputs = Environment.GetEnvironmentVariable("AINATIVE_GRID_INPUTS") ?? Application.streamingAssetsPath;
            Directory.CreateDirectory(_output);
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "grid.scenario.json")),
                File.ReadAllBytes(Path.Combine(_inputs, "grid.scenario.json")));
#if UNITY_EDITOR
            string? reload = Environment.GetEnvironmentVariable("AINATIVE_DOMAIN_RELOAD");
            if (reload != null)
            {
                bool disabled = UnityEditor.EditorSettings.enterPlayModeOptionsEnabled &&
                    UnityEditor.EditorSettings.enterPlayModeOptions.HasFlag(UnityEditor.EnterPlayModeOptions.DisableDomainReload);
                Assert.AreEqual(reload == "off", disabled);
            }
#endif
        }

        [UnityTest] public IEnumerator FastPresentationMatchesActualOperations() => Record(false);
        [UnityTest] public IEnumerator SlowPresentationMatchesActualOperations() => Record(true);

        [UnityTest]
        public IEnumerator MovementAndHitFeedbackUseRenderedFramesWithoutExtraTicks()
        {
            float previousDelta = Time.captureDeltaTime;
            try
            {
                Time.captureDeltaTime = .05f;
                _game.SecondsPerEvent = .4f;
                void Tile(int x, int y) => _game.HandlePointer(Camera.main.WorldToScreenPoint(new Vector3(x, y)));
                var mover = GameObject.Find("A1").GetComponent<SpriteRenderer>();
                var target = GameObject.Find("B2").GetComponent<SpriteRenderer>();
                Tile(0, 0);
                Tile(1, 0);
                bool interpolated = false;
                for (int i = 0; _game.Frames.PendingCount != 0 && i < 30; i++)
                {
                    yield return null;
                    float x = mover.transform.localPosition.x;
                    interpolated |= x > 0 && x < 1;
                }
                Assert.True(interpolated);
                Assert.AreEqual(1, mover.transform.localPosition.x);
                Assert.AreEqual(1, _game.Session.Tick);
                Tile(2, 0);
                bool flash = false;
                for (int i = 0; _game.Frames.PendingCount != 0 && i < 30; i++)
                {
                    yield return null;
                    flash |= target.color == Color.white;
                }
                Assert.True(flash);
                Assert.AreNotEqual(Color.white, target.color);
                Assert.AreEqual(2, _game.Frames.Presented.Tick);
            }
            finally
            {
                Time.captureDeltaTime = previousDelta;
            }
        }

        private IEnumerator Record(bool slow)
        {
            _game.SecondsPerEvent = slow ? .4f : 0;
            var commands = CommandFile.Read(JsonData.Parse(File.ReadAllBytes(Path.Combine(_inputs, "grid.commands.json"))), _game.Module);
            var simulated = new List<Observation<GridGameView>> { _game.Frames.Presented };
            var operations = new List<int>();
            var presentations = new List<int>();
            RecordedRun? result = null;
            int backlog = 0;
            var session = _game.Session;
            var steps = HostVerification.RecordSteps(_game.Module, _game.Configuration, session, commands, Identity(),
                command => _game.Submit(_game.Module.DecodePlayCommand(command)),
                () =>
                {
                    var frame = _game.Advance();
                    simulated.Add(frame.Observation);
                    return session.EncodeTick(frame);
                }, recorded => result = recorded);

            void CheckScreen()
            {
                var frame = _game.Frames.Presented;
                if (frame.Tick == presentations.Count)
                {
                    Assert.AreSame(simulated[(int)frame.Tick], frame);
                    presentations.Add(Time.frameCount);
                }
                else
                {
                    Assert.AreEqual(presentations.Count - 1, frame.Tick, "skipped a presented Tick");
                }
            }
            foreach (var checkpoint in steps)
            {
                CheckScreen();
                if (operations.Count > 0)
                {
                    Assert.Greater(Time.frameCount, operations.Last());
                }
                operations.Add(Time.frameCount);
                backlog = Math.Max(backlog, _game.Frames.PendingCount);
                Assert.AreEqual(_game.Session.Tick, StateCodec.Long(checkpoint.GetProperty("runtime"), "tick"));
                yield return null;
            }
            float deadline = Time.realtimeSinceStartup + 20;
            while (_game.Frames.PendingCount != 0)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline);
                CheckScreen();
                yield return null;
            }
            CheckScreen();
            Assert.AreEqual(9, presentations.Count);
            Assert.AreEqual(16, operations.Count);
            Assert.AreEqual(slow, backlog > 1);
            Assert.AreEqual(6, result!.Accepted);
            Assert.AreEqual(1, result.Rejected);
            Assert.AreEqual(Side.A, _game.Frames.Presented.State.Winner);
            foreach (var checkpoint in result.Replay.GetProperty("checkpoints").EnumerateArray())
            {
                if (checkpoint.GetProperty("result").GetProperty("kind").GetString() != "tick")
                {
                    continue;
                }
                int tick = (int)StateCodec.Long(checkpoint.GetProperty("runtime"), "tick");
                var view = simulated[tick].State;
                var state = checkpoint.GetProperty("state");
                Assert.AreEqual(view.ActiveSide.ToString(), state.GetProperty("activeSide").GetString());
                Assert.AreEqual(view.Winner?.ToString(), state.GetProperty("winner").GetString());
                Assert.AreEqual(view.TurnIndex, StateCodec.Long(state, "turnIndex"));
                Assert.AreEqual(view.ActionPoints, state.GetProperty("actionPoints").GetInt32());
                var units = state.GetProperty("units");
                Assert.AreEqual(view.Units.Count, units.GetArrayLength());
                for (int i = 0; i < view.Units.Count; i++)
                {
                    var unit = view.Units[i];
                    var saved = units[i];
                    Assert.AreEqual(unit.Id, saved.GetProperty("id").GetInt32());
                    Assert.AreEqual(unit.Side.ToString(), saved.GetProperty("side").GetString());
                    Assert.AreEqual(unit.Health, saved.GetProperty("health").GetInt32());
                    Assert.AreEqual(unit.Position.X, saved.GetProperty("position").GetProperty("x").GetInt32());
                    Assert.AreEqual(unit.Position.Y, saved.GetProperty("position").GetProperty("y").GetInt32());
                }
            }
            string speed = slow ? "slow" : "fast";
            File.WriteAllBytes(Path.Combine(_output, $"play-{speed}.replay.json"), JsonData.CanonicalBytes(result.Replay));
            File.WriteAllText(Path.Combine(_output, $"play-{speed}.frames.json"), JsonData.Write(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("isPlaying", Application.isPlaying);
                w.WriteString("scene", _game.gameObject.scene.path);
                w.WriteNumber("peakQueuedTicks", backlog);
                w.WriteStartArray("operationFrames");
                foreach (int value in operations) { w.WriteNumberValue(value); }
                w.WriteEndArray();
                w.WriteStartArray("presentedFrames");
                foreach (int value in presentations) { w.WriteNumberValue(value); }
                w.WriteEndArray();
                w.WriteEndObject();
            }).GetRawText());
        }

        private HostEnvironment Identity()
        {
            var assemblies = new List<Assembly>
            {
                typeof(GridPlayTests).Assembly, typeof(GridGameBootstrap).Assembly, typeof(PlayHost<,>).Assembly,
                typeof(Application).Assembly, typeof(Button).Assembly, typeof(RuntimeState).Assembly, typeof(IGameModule).Assembly,
                typeof(JsonDocument).Assembly, typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly,
                typeof(System.IO.Pipelines.PipeReader).Assembly
            };
            assemblies.AddRange(_game.Module.CodeTypes.Select(t => t.Assembly));
            foreach (var reference in typeof(JsonDocument).Assembly.GetReferencedAssemblies())
            {
                if (reference.Name == "Microsoft.Bcl.AsyncInterfaces" || reference.Name == "System.Runtime.CompilerServices.Unsafe")
                {
                    assemblies.Add(Assembly.Load(reference));
                }
            }
            Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith("AiNative.Physics", StringComparison.Ordinal)));
            return new HostEnvironment("Unity Play " + Application.unityVersion + " / Mono " + Environment.Version,
                Application.platform.ToString(), RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), JsonData.Write(w =>
                {
                    w.WriteStartObject();
                    foreach (var a in assemblies.Distinct())
                    {
                        w.WriteString(a.GetName().Name, JsonData.Hash(File.ReadAllBytes(a.Location)));
                    }
                    w.WriteEndObject();
                }));
        }

        [UnityTest]
        public IEnumerator BoardAndHudFinishMatchAndReleaseOnRestartAndUnload()
        {
            _game.SecondsPerEvent = 0;
            void Tile(int x, int y) => _game.HandlePointer(Camera.main.WorldToScreenPoint(new Vector3(x, y)));
            var hud = Object.FindFirstObjectByType<GridHud>();
            void Press(Button button)
            {
                var screen = RectTransformUtility.WorldToScreenPoint(null, button.transform.TransformPoint(((RectTransform)button.transform).rect.center));
                var data = new PointerEventData(EventSystem.current) { position = screen, button = PointerEventData.InputButton.Left };
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(data, hits);
                Assert.AreSame(button.gameObject, ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject));
                long before = _game.Session.Tick;
                _game.HandlePointer(screen);
                Assert.AreEqual(before, _game.Session.Tick);
                ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerClickHandler);
            }
            Tile(0, 0);
            Assert.AreEqual(1, _game.Selected);
            yield return null;
            Assert.AreEqual(0, _game.Session.Tick);
            // Put a real HUD target over a legal move: bypassing UI raycasting would advance.
            RectTransform overlay = (RectTransform)hud.EndTurn.transform;
            Vector3 originalPosition = overlay.position;
            var destination = Camera.main.WorldToScreenPoint(new Vector3(1, 0));
            overlay.position += (Vector3)((Vector2)destination - RectTransformUtility.WorldToScreenPoint(null, overlay.TransformPoint(overlay.rect.center)));
            Canvas.ForceUpdateCanvases();
            Assert.True(hud.BlocksPointer(destination));
            _game.HandlePointer(destination);
            Assert.AreEqual(0, _game.Session.Tick);
            overlay.position = originalPosition;
            Canvas.ForceUpdateCanvases();
            Tile(1, 0);
            yield return null;
            Tile(2, 0);
            yield return null;
            Assert.AreEqual(2, _game.Frames.Presented.Tick);
            Assert.AreEqual(2, _game.Frames.Presented.State.Units.Single(u => u.Id == 2).Health);
            string png = Path.Combine(_output, "game-tick-2.png");
            if (File.Exists(png)) { File.Delete(png); }
            ScreenCapture.CaptureScreenshot(png);
            float deadline = Time.realtimeSinceStartup + 15;
            while (!File.Exists(png) || new FileInfo(png).Length < 100)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "Game view capture timed out");
                yield return null;
            }
            Assert.AreEqual(2, _game.Frames.Presented.Tick);
            Press(hud.EndTurn);
            yield return null;
            Tile(2, 0);
            Tile(1, 0);
            yield return null;
            Press(hud.EndTurn);
            yield return null;
            Tile(1, 0);
            Tile(2, 0);
            yield return null;
            Assert.AreEqual(Side.A, _game.Frames.Presented.State.Winner);
            var old = _game.Session;
            var queue = _game.Frames;
            _game.Advance();
            var stale = queue.BeginNext() ?? throw new InvalidOperationException("missing queued frame");
            JsonElement oversized = JsonData.Write(w =>
            {
                w.WriteStartObject();
                foreach (var property in _game.Configuration.EnumerateObject())
                {
                    if (property.Name == "width" || property.Name == "height")
                    {
                        w.WriteNumber(property.Name, 100000);
                    }
                    else
                    {
                        property.WriteTo(w);
                    }
                }
                w.WriteEndObject();
            });
            Assert.Throws<InvalidOperationException>(() => _game.ReplaceMatch(oversized));
            Assert.AreSame(old, _game.Session);
            Assert.True(queue.IsActive(stale));
            var camera = Camera.main;
            camera.orthographic = false;
            try
            {
                Assert.Throws<InvalidOperationException>(() => _game.ReplaceMatch(_game.Configuration));
                Assert.AreSame(old, _game.Session);
                Assert.True(queue.IsActive(stale));
                Assert.AreEqual(stale.Tick + 1, _game.Advance().Tick);
            }
            finally
            {
                camera.orthographic = true;
            }
            Press(hud.Restart);
            Assert.AreEqual(0, _game.Frames.Presented.Tick);
            Assert.False(queue.Complete(stale));
            Assert.Throws<ObjectDisposedException>(() => old.CaptureObservation());
            var next = _game.Session;
            _game.enabled = false;
            Assert.Throws<ObjectDisposedException>(() => next.CaptureObservation());
            _game.enabled = true;
            next = _game.Session;
            yield return SceneManager.LoadSceneAsync("GridGame");
            Assert.Throws<ObjectDisposedException>(() => next.CaptureObservation());
        }
    }
}
