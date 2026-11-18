#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using AiNative.Hosting;
using AiNative.Physics;
using AiNative.Physics.Jolt;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.Examples.PhysicsPlay
{
    public sealed class PhysicsPlayerSmoke : MonoBehaviour
    {
        [Serializable]
        private sealed class Result
        {
            public bool success;
            public string message = "";
            public long restoredTick;
            public int restoredBodies;
            public uint worldsAfterDisconnect;
        }

        private double _nextSeconds;

        private IEnumerator Start()
        {
            string? resultPath = Argument("-playerSmokeResult");
            if (resultPath == null) { yield break; }
            var result = new Result();
            var run = Verify(result, resultPath);
            while (true)
            {
                bool more;
                object? current = null;
                try
                {
                    more = run.MoveNext();
                    if (more) { current = run.Current; }
                }
                catch (Exception error)
                {
                    result.message = error.ToString();
                    Debug.LogException(error);
                    var scene = FindFirstObjectByType<SandboxScene>();
                    if (scene != null) { scene.enabled = false; }
                    break;
                }
                if (!more) { result.success = true; break; }
                yield return current;
            }
            File.WriteAllText(resultPath, JsonUtility.ToJson(result));
            Application.Quit(result.success ? 0 : 1);
        }

        private IEnumerator Verify(Result result, string resultPath)
        {
            var scene = FindFirstObjectByType<SandboxScene>();
            Check(scene != null, "PhysicsSandbox host is missing.");
            scene!.Paused = true;
            scene.FrameSeconds = () => { double value = _nextSeconds; _nextSeconds = 0; return value; };
            Check(scene.Session.Tick == 0 && JoltWorldFactory.ActiveWorldCount == 1,
                "Initial physical world is invalid.");
            scene.Spawn(ShapeKind.Sphere);
            scene.Spawn(ShapeKind.Box);
            Check(scene.Session.Pending == 2, "Paused inputs were not queued.");
            bool saveRejected = false;
            try { scene.Save(); }
            catch (ProtocolException error) when (error.Code == "PendingCommands") { saveRejected = true; }
            Check(saveRejected, "Save accepted pending inputs.");

            scene.Paused = false;
            _nextSeconds = 1d / 60d;
            for (int frame = 0; scene.Session.Tick != 1 && frame < 300; frame++) { yield return null; }
            Check(scene.Session.Tick == 1 && scene.Session.Processed == 2, "First physical Tick did not consume both inputs.");
            scene.Paused = true;
            Check(scene.Display.Count == 4, "Spawned bodies are not visible.");
            var snapshot = scene.Save();

            scene.Remove(3);
            scene.Paused = false;
            _nextSeconds = 1d / 60d;
            for (int frame = 0; scene.Session.Tick != 2 && frame < 300; frame++) { yield return null; }
            Check(scene.Session.Tick == 2 && scene.Display.Count == 3, "Remove did not reach the scene.");
            scene.Load(snapshot);
            Check(scene.Paused && scene.Session.Tick == 1 && scene.Frames.Current.Tick == 1,
                "Restore did not return to the saved Tick.");
            Check(scene.Display.Count == 4 && JoltWorldFactory.ActiveWorldCount == 1,
                "Restore did not rebuild exactly one physical world.");
            Check(scene.Frames.Current.State.Select(body => body.Id).SequenceEqual(new ulong[] { 1, 2, 3, 4 }),
                "Restored body identities differ.");
            result.restoredTick = scene.Session.Tick;
            result.restoredBodies = scene.Display.Count;
            string screenshot = Path.ChangeExtension(resultPath, ".png");
            CaptureScene(screenshot);

            scene.enabled = false;
            result.worldsAfterDisconnect = JoltWorldFactory.ActiveWorldCount;
            Check(result.worldsAfterDisconnect == 0, "Jolt world remained after scene disconnect.");
            result.message = "Physics Player pause, input, save, restore, screenshot and cleanup passed.";
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }

        private static void CaptureScene(string path)
        {
            var camera = Camera.main;
            if (camera == null) { throw new InvalidOperationException("Main camera is missing."); }
            var target = RenderTexture.GetTemporary(640, 360, 24);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(640, 360, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Destroy(pixels);
            }
        }

        private static string? Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
