#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AiNative.Core;
using AiNative.Hosting;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using AiNative.PhysicsSandbox;
using AiNative.PhysicsSandbox.Hosting;
using AiNative.Unity;
using UnityEngine;
using NVector = System.Numerics.Vector3;

namespace AiNative.Examples.PhysicsPlay
{
    public sealed class SandboxScene : PlaySceneHost<SandboxCommand, IReadOnlyList<ObservedBody>>
    {
        internal PhysicsSandboxModule Module { get; } = new PhysicsSandboxModule(new JoltWorldFactory(), "ainative_jolt");
        internal SandboxHost Session { get; private set; } = null!;
        internal HostEnvironment EnvironmentIdentity { get; private set; } = null!;
        internal JsonElement Configuration { get; private set; }
        internal SandboxDisplay Display { get; private set; } = null!;
        internal RealtimePresentationBuffer<IReadOnlyList<ObservedBody>> Frames => Host.Realtime!;
        internal HostTraceRecorder? Recorder { get; set; }
        internal Func<double> FrameSeconds { get; set; } = () => Time.deltaTime;
        internal event Action<PresentationEvents>? EventsPresented;
        internal bool Paused { get; set; }
        internal string Feedback { get; private set; } = "";
        internal int Contacts { get; private set; }
        private GameRegistry _registry = null!;
        private string _savePath = "";
        [SerializeField] private SandboxHud hud = null!;

        protected override void ConnectScene()
        {
            _registry = new GameRegistry(Module);
            EnvironmentIdentity = SandboxIdentity.Read(Module);
            _savePath = Path.Combine(Application.persistentDataPath, "physics-v2.snapshot.json");
            Restart();
            hud.Bind(this);
        }

        internal void Restart()
        {
            var scene = JsonData.Parse(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "physics.scenario.json")));
            var configuration = _registry.ReadScenario(scene).Configuration;
            Replace(() => Module.CreatePlay(configuration, 42), configuration);
            Paused = false;
        }

        internal JsonElement Save() => StateCodec.Encode(Module, Configuration, Session, EnvironmentIdentity);

        internal void Load(JsonElement json)
        {
            var snapshot = StateCodec.Read(json, _registry);
            JsonData.Require(JsonData.Hash(snapshot.Configuration) == JsonData.Hash(Configuration),
                "ExpectedConfigMismatch", "$.configuration");
            Replace(() => (SandboxHost)StateCodec.Restore(snapshot, EnvironmentIdentity), snapshot.Configuration);
            Paused = true;
        }

        private void Replace(Func<SandboxHost> create, JsonElement configuration)
        {
            SandboxHost? candidate = null;
            SandboxDisplay? staged = null;
            try
            {
                Host.Replace(() =>
                {
                    candidate = create();
                    try
                    {
                        staged = new SandboxDisplay(transform);
                        var initial = candidate.CaptureObservation();
                        staged.Draw(initial, initial, 1);
                        if (Camera.main == null) { throw new InvalidOperationException("A MainCamera is required."); }
                        return candidate;
                    }
                    catch { candidate.Dispose(); throw; }
                });
            }
            catch
            {
                staged?.Dispose();
                if (Host.IsFaulted && Host.Realtime == null) { ReleaseDisplay(); }
                throw;
            }
            ReleaseDisplay();
            Display = staged!;
            Session = candidate!;
            Configuration = configuration;
            Display.Root.SetActive(true);
            Feedback = "";
            Contacts = 0;
            hud.Target.text = "2";
        }

        private void ReleaseDisplay()
        {
            Display?.Dispose();
            Display = null!;
            Session = null!;
            Recorder = null;
        }

        internal long Submit(SandboxCommand command)
        {
            long sequence = Host.Submit(command);
            Recorder?.Submitted(Module.EncodePlayCommand(command), sequence);
            Feedback = $"Input {sequence} queued";
            return sequence;
        }

        protected override void PumpFrame() => Host.Pump(FrameSeconds(), Paused || Time.timeScale == 0,
            Recorder == null ? null : RecordStep);

        private void RecordStep(TickResult<IReadOnlyList<ObservedBody>> frame) =>
            Recorder!.Advanced(Session.EncodeTick(frame));

        protected override void PresentFrame()
        {
            PresentationEvents? batch;
            while ((batch = Frames.TakeEvents()) != null)
            {
                foreach (var input in batch.Inputs)
                {
                    Feedback = input.Result.Succeeded ? $"Input {input.Sequence} applied at tick {batch.Tick}" :
                        $"Tick {batch.Tick}   {input.Result.Error}";
                }
                foreach (var value in batch.Events)
                {
                    if (value is ContactChanged contact && contact.Contact.Started) { Contacts++; }
                }
                EventsPresented?.Invoke(batch);
            }
            Display.Draw(Frames.Previous, Frames.Current, (float)Host.Interpolation);
            hud.Show(this);
        }

        internal void SaveToFile(string path)
        {
            byte[] bytes = JsonData.CanonicalBytes(Save());
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) { File.Replace(temp, path, null); }
                else { File.Move(temp, path); }
            }
            finally { if (File.Exists(temp)) { File.Delete(temp); } }
            Feedback = $"Saved tick {Session.Tick}";
        }

        internal void LoadFile(string path) => Load(JsonData.Parse(File.ReadAllBytes(path)));

        protected override void DisconnectScene()
        {
            hud?.Unbind();
            EventsPresented = null;
            ReleaseDisplay();
        }

        internal void SaveSlot() => TryAction(() => SaveToFile(_savePath));
        internal void LoadSlot() => TryAction(() => LoadFile(_savePath));
        internal void NewWorld() => TryAction(Restart);

        internal long Impulse(ulong id, NVector value) => Submit(new ApplyImpulse(id, value));
        internal long Remove(ulong id) => Submit(new RemoveBody(id));

        internal void Spawn(ShapeKind shape)
        {
            int lane = (int)(Session.Submitted % 5) - 2;
            Submit(new SpawnBody(shape, new NVector(.45f), new NVector(lane, 4, 0)));
        }

        private void TryAction(Action action)
        {
            try { action(); }
            catch (Exception error) { Feedback = error is ProtocolException protocol ? protocol.Code : error.Message; }
        }
    }
}
