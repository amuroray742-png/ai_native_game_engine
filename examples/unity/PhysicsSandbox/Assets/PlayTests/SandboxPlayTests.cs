#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiNative.Hosting;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using AiNative.Examples.PhysicsPlay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using NVector = System.Numerics.Vector3;

namespace AiNative.Examples.PhysicsPlayTests
{
    public sealed class SandboxPlayTests
    {
        private SandboxScene _scene = null!;
        private string _output = "";
        private double _nextSeconds;
        private int _consumedFrame;
        private JsonElement _snapshot90, _snapshot91;
        private readonly List<JsonElement> _frames = new List<JsonElement>();
        private readonly List<long> _sequences = new List<long>();
        private readonly List<PresentationEvents> _events = new List<PresentationEvents>();

        [UnitySetUp]
        public IEnumerator Open()
        {
            _events.Clear();
            _frames.Clear();
            _sequences.Clear();
            _nextSeconds = 0;
            yield return SceneManager.LoadSceneAsync("PhysicsSandbox");
            _scene = UnityEngine.Object.FindFirstObjectByType<SandboxScene>();
            _scene.FrameSeconds = () =>
            {
                _consumedFrame = Time.frameCount;
                double elapsed = _nextSeconds;
                _nextSeconds = 0;
                return elapsed;
            };
            _scene.Restart();
            _scene.EventsPresented += _events.Add;
            _output = Environment.GetEnvironmentVariable("AINATIVE_PHYSICS_EVIDENCE")
                ?? Path.Combine(Application.temporaryCachePath, "physics-evidence");
            Directory.CreateDirectory(_output);
            Assert.True(Application.isPlaying);
            Assert.AreEqual(1, JoltWorldFactory.ActiveWorldCount);
#if UNITY_EDITOR
            string? reload = Environment.GetEnvironmentVariable("AINATIVE_DOMAIN_RELOAD");
            if (reload != null)
            {
                Assert.AreEqual(reload == "off", UnityEditor.EditorSettings.enterPlayModeOptionsEnabled &&
                    UnityEditor.EditorSettings.enterPlayModeOptions.HasFlag(UnityEditor.EnterPlayModeOptions.DisableDomainReload));
            }
#endif
        }

        private IEnumerator Frame(double steps, long expectedTick)
        {
            int previousFrame = _consumedFrame;
            long before = _scene.Session.Tick;
            _nextSeconds = steps / 60d;
            do { yield return null; } while (_consumedFrame == previousFrame);
            Assert.AreEqual(expectedTick, _scene.Session.Tick);
            Assert.AreEqual(expectedTick, _scene.Frames.Current.Tick);
            Assert.AreEqual(expectedTick, _scene.Display.ToTick);
            _frames.Add(JsonData.Write(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("frame", _consumedFrame);
                w.WriteNumber("stepsProvided", steps);
                w.WriteBoolean("paused", _scene.Paused);
                w.WriteNumber("before", before);
                w.WriteNumber("after", _scene.Session.Tick);
                w.WriteNumber("renderFromTick", _scene.Display.FromTick);
                w.WriteNumber("renderToTick", _scene.Display.ToTick);
                w.WriteNumber("renderFraction", _scene.Display.Fraction);
                w.WriteStartArray("submitted");
                foreach (long sequence in _sequences) { w.WriteNumberValue(sequence); }
                w.WriteEndArray();
                w.WriteEndObject();
            }));
            _sequences.Clear();
        }

        private void Begin()
        {
            _events.Clear();
            _frames.Clear();
            _sequences.Clear();
            _scene.Recorder = new HostTraceRecorder(_scene.Module, _scene.Configuration, _scene.Session, _scene.EnvironmentIdentity);
        }

        private void End(string name)
        {
            RecordedRun run = _scene.Recorder!.Finish();
            _scene.Recorder = null;
            Write(name + ".replay.json", run.Replay);
            Write(name + ".frames.json", JsonData.Write(w =>
            {
                w.WriteStartArray();
                foreach (var frame in _frames) { frame.WriteTo(w); }
                w.WriteEndArray();
            }));
            var batches = run.Replay.GetProperty("checkpoints").EnumerateArray().Select(c => c.GetProperty("result"))
                .Where(r => r.GetProperty("kind").GetString() == "tick" &&
                    (r.GetProperty("inputs").GetArrayLength() != 0 || r.GetProperty("events").GetArrayLength() != 0)).ToArray();
            Assert.AreEqual(batches.Length, _events.Count);
            for (int i = 0; i < batches.Length; i++)
            {
                var result = batches[i];
                var batch = _events[i];
                Assert.AreEqual(StateCodec.Long(result, "tick"), batch.Tick);
                Assert.AreEqual(result.GetProperty("inputs").GetArrayLength(), batch.Inputs.Count);
                Assert.AreEqual(result.GetProperty("events").GetArrayLength(), batch.Events.Count);
                for (int j = 0; j < batch.Inputs.Count; j++)
                {
                    Assert.AreEqual(StateCodec.Long(result.GetProperty("inputs")[j], "sequence"), batch.Inputs[j].Sequence);
                }
                for (int j = 0; j < batch.Events.Count; j++)
                {
                    var json = result.GetProperty("events")[j];
                    if (batch.Events[j] is AiNative.PhysicsSandbox.ContactChanged contact)
                    {
                        Assert.AreEqual(contact.Contact.Started ? "ContactStarted" : "ContactEnded", json.GetProperty("type").GetString());
                        Assert.AreEqual(contact.Contact.First.ToString(), json.GetProperty("first").GetString());
                        Assert.AreEqual(contact.Contact.Second.ToString(), json.GetProperty("second").GetString());
                        Assert.AreEqual(contact.Contact.FirstShape.ToString(), json.GetProperty("firstShape").GetString());
                        Assert.AreEqual(contact.Contact.SecondShape.ToString(), json.GetProperty("secondShape").GetString());
                    }
                    else
                    {
                        var changed = (AiNative.PhysicsSandbox.BodyChanged)batch.Events[j];
                        Assert.AreEqual(changed.Spawned ? "BodySpawned" : "BodyRemoved", json.GetProperty("type").GetString());
                        Assert.AreEqual(changed.Id.ToString(), json.GetProperty("id").GetString());
                    }
                }
            }
        }

        private void Write(string name, JsonElement value) =>
            File.WriteAllBytes(Path.Combine(_output, name), JsonData.CanonicalBytes(value));

        private IEnumerator Standard(int maxSteps, int startTick, string name)
        {
            var commands = CommandFile.Read(JsonData.Parse(File.ReadAllBytes(
                Path.Combine(Application.streamingAssetsPath, "physics.commands.json"))), _scene.Module).Operations;
            int index = 0, tick = 0;
            while (tick < startTick)
            {
                if (!commands[index++].Command.HasValue) { tick++; }
            }
            Begin();
            while (index < commands.Count)
            {
                while (index < commands.Count && commands[index].Command is JsonElement cmd)
                {
                    _sequences.Add(_scene.Submit(_scene.Module.DecodePlayCommand(cmd)));
                    index++;
                }
                int steps = 0;
                while (index < commands.Count && !commands[index].Command.HasValue && steps < maxSteps)
                {
                    index++; steps++; tick++;
                    if (tick == 90 || tick == 91) { break; }
                }
                Assert.Greater(steps, 0);
                yield return Frame(steps, tick);
                if (startTick == 0 && tick == 90) { _snapshot90 = _scene.Save(); }
                if (startTick == 0 && tick == 91) { _snapshot91 = _scene.Save(); }
            }
            Assert.AreEqual(240, tick);
            End(name);
        }

        [UnityTest] public IEnumerator UniformSteps() => Standard(1, 0, "uniform");

        [UnityTest]
        public IEnumerator CatchupAndRestoreBothBoundaries()
        {
            yield return Standard(4, 0, "catchup");
            Assert.Greater(_scene.Contacts, 0);
            var old = _scene.Frames;
            var stale = _events.Last();
            foreach (int tick in new[]{90, 91})
            {
                JsonElement snapshot = tick == 90 ? _snapshot90 : _snapshot91;
                Write("tick-" + tick + ".snapshot.json", snapshot);
                _scene.Load(snapshot);
                yield return Frame(0, tick);
                Assert.True(_scene.Paused);
                Assert.False(old.IsActive(stale));
                Assert.AreSame(_scene.Frames.Previous, _scene.Frames.Current);
                Assert.True(_scene.Frames.IsAt(_scene.Frames.Current));
                CheckDisplay(tick);
                string png = Path.Combine(_output, "restored-" + tick + ".png");
                if (File.Exists(png)) { File.Delete(png); }
                ScreenCapture.CaptureScreenshot(png);
                float deadline = Time.realtimeSinceStartup + 15;
                while (!File.Exists(png) || new FileInfo(png).Length < 100)
                {
                    Assert.Less(Time.realtimeSinceStartup, deadline);
                    yield return null;
                }
                Assert.AreEqual(tick, _scene.Session.Tick);
                _scene.Paused = false;
                yield return Standard(4, tick, "from-" + tick);
            }
            _scene.Load(_snapshot90);
            CheckDisplay(90);
            Assert.AreEqual(1, JoltWorldFactory.ActiveWorldCount);
        }

        private void CheckDisplay(int tick)
        {
            var state = _scene.Frames.Current.State;
            CollectionAssert.AreEqual(new ulong[] {1, 2, tick == 90 ? 3UL : 4UL}, state.Select(b => b.Id).ToArray());
            Assert.AreEqual(tick, _scene.Display.FromTick);
            Assert.AreEqual(tick, _scene.Display.ToTick);
            Assert.AreEqual(state.Count, _scene.Display.Count);
            Assert.AreEqual(tick == 90 ? ShapeKind.Box : ShapeKind.Sphere, state[2].Shape);
            Assert.AreEqual(.4f, state[2].Size.X);
            foreach (var body in state)
            {
                var item = _scene.Display.Body(body.Id);
                Assert.True(item.activeInHierarchy);
                StringAssert.Contains(body.Shape == ShapeKind.Box ? "Cube" : "Sphere", item.GetComponent<MeshFilter>().sharedMesh.name);
                var scale = body.Shape == ShapeKind.Box ? 2 * SandboxDisplay.Vector(body.Size) : Vector3.one * (2 * body.Size.X);
                Assert.Less(Vector3.Distance(scale, item.transform.localScale), 1e-5f);
                Assert.Less(Vector3.Distance(SandboxDisplay.Vector(body.Position), item.transform.position), 1e-5f);
                Assert.Less(Quaternion.Angle(SandboxDisplay.Rotation(body.Rotation), item.transform.rotation), .05f);
            }
        }

        [UnityTest]
        public IEnumerator PausedInputAndDebt()
        {
            Begin();
            yield return Frame(10, 4);
            var hud = UnityEngine.Object.FindFirstObjectByType<SandboxHud>();
            SandboxHudTests.Click("Pause");
            hud.Target.text = "2";
            SandboxHudTests.Click("Impulse X");
            _sequences.Add(_scene.Session.Submitted);
            yield return Frame(30, 4);
            hud.Target.text = "999";
            SandboxHudTests.Click("Remove");
            _sequences.Add(_scene.Session.Submitted);
            yield return Frame(30, 4);
            hud.Target.text = "2";
            SandboxHudTests.Click("Impulse Y");
            _sequences.Add(_scene.Session.Submitted);
            yield return Frame(30, 4);
            Assert.AreEqual(0, _scene.Session.Processed);
            Assert.AreEqual(3, _scene.Session.Pending);
            Assert.AreEqual("PendingCommands", Assert.Throws<ProtocolException>(() => _scene.Save())!.Code);
            SandboxHudTests.Click("Pause");
            yield return Frame(0, 8);
            yield return Frame(0, 10);
            yield return Frame(0, 10);
            var inputs = _events.Single().Inputs;
            Assert.AreEqual(5, _events.Single().Tick);
            CollectionAssert.AreEqual(new[]{"", "UnknownBody", ""}, inputs.Select(i => i.Result.Error ?? "").ToArray());
            End("pause");
        }

        [TestCase("-0", int.MinValue, "-0")]
        [TestCase("7.038531E-26", 0x15ae43fd, "7.038531E-26")]
        [TestCase("1.0000000596046447753906250000000001", 0x3f800001, "1.0000001")]
        [TestCase("1.0000001788139343261718749999999999", 0x3f800001, "1.0000001")]
        public void CommandFloatHasTheCliBitsAndSpelling(string token, int bits, string canonical)
        {
            string json = "{\"type\":\"Impulse\",\"id\":\"2\",\"impulse\":[" + token + ",0,0]}";
            var command = (AiNative.PhysicsSandbox.ApplyImpulse)_scene.Module.DecodePlayCommand(
                JsonData.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
            Assert.AreEqual(bits, BitConverter.SingleToInt32Bits(command.Impulse.X));
            Assert.AreEqual(canonical, _scene.Module.EncodePlayCommand(command).GetProperty("impulse")[0].GetRawText());
        }

        [Test]
        public void CompletedStepCannotRecursivelyPumpOrReplace()
        {
            using var host = new AiNative.Unity.PlayHost<AiNative.PhysicsSandbox.SandboxCommand, IReadOnlyList<ObservedBody>>();
            host.Replace(() => _scene.Module.CreatePlay(_scene.Configuration, 42));
            var ticks = new List<long>();
            host.Pump(10d / 60, false, frame =>
            {
                ticks.Add(frame.Tick);
                Assert.Throws<InvalidOperationException>(() => host.Pump(0, false));
                Assert.Throws<InvalidOperationException>(() => host.Submit(new AiNative.PhysicsSandbox.RemoveBody(2)));
                Assert.Throws<InvalidOperationException>(() => host.Replace(() => _scene.Module.CreatePlay(_scene.Configuration, 42)));
                Assert.Throws<InvalidOperationException>(() => host.Dispose());
            });
            CollectionAssert.AreEqual(new long[] {1,2,3,4}, ticks);
            Assert.Throws<ArgumentException>(() => host.Pump(0, false, _ => throw new ArgumentException("observer failed")));
            Assert.True(host.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => host.Pump(0, false));
        }

        [UnityTest]
        public IEnumerator FileSaveAndPausedRestore()
        {
            // Use the same atomic write and load path as the HUD, under this run's output directory.
            string slot = Path.Combine(_output, "hud-slot.snapshot.json");
            _scene.Impulse(2, NVector.UnitY);
            yield return Frame(1, 1);
            _scene.SaveToFile(slot);
            yield return Frame(1, 2);
            _scene.SaveToFile(slot);
            _scene.Paused = true;
            _scene.Remove(2);
            Assert.AreEqual("PendingCommands", Assert.Throws<ProtocolException>(() => _scene.SaveToFile(slot))!.Code);
            _scene.LoadFile(slot);
            Assert.True(_scene.Paused);
            Assert.AreEqual(2, _scene.Session.Tick);
            Assert.AreEqual(0, _scene.Session.Pending);
            Assert.AreEqual(2, _scene.Display.Count);
        }

        [UnityTest]
        public IEnumerator FractionalPauseAndFailedReplacement()
        {
            yield return Frame(.25, 0);
            _scene.Impulse(2, NVector.UnitX);
            _scene.Paused = true;
            _scene.Impulse(2, NVector.UnitY);
            yield return Frame(10, 0);
            _scene.Paused = false;
            yield return Frame(.25, 0);
            yield return Frame(.5, 1);
            CollectionAssert.AreEqual(new long[]{1,2}, _events.Single().Inputs.Select(i => i.Sequence).ToArray());
            var previous = _scene.Session;
            var display = _scene.Display;
            var snapshot = _scene.Save();
            string json = snapshot.GetRawText();
            var identity = _scene.EnvironmentIdentity;
            Assert.AreEqual("EnvironmentMismatch", Assert.Throws<ProtocolException>(() => _scene.Load(
                JsonData.Parse(System.Text.Encoding.UTF8.GetBytes(json.Replace(identity.Runtime, "other-runtime")))))!.Code);
            string hash = snapshot.GetProperty("stateHash").GetString()!;
            Assert.AreEqual("StateHashMismatch", Assert.Throws<ProtocolException>(() => _scene.Load(
                JsonData.Parse(System.Text.Encoding.UTF8.GetBytes(json.Replace(hash, new string('0',64))))))!.Code);
            var camera = Camera.main;
            camera.tag = "Untagged";
            try { Assert.Throws<InvalidOperationException>(() => _scene.Load(snapshot)); }
            finally { camera.tag = "MainCamera"; }
            Assert.AreSame(previous, _scene.Session);
            Assert.AreSame(display, _scene.Display);
            Assert.AreEqual(1, _scene.Session.Tick);
            Assert.AreEqual(1, JoltWorldFactory.ActiveWorldCount);
            yield return Frame(1, 2);
            _scene.enabled = false;
            Assert.AreEqual(0, JoltWorldFactory.ActiveWorldCount);
            _scene.enabled = true;
            Assert.AreEqual(1, JoltWorldFactory.ActiveWorldCount);
            Assert.AreEqual(0, _scene.Session.Tick);
        }
    }
}
