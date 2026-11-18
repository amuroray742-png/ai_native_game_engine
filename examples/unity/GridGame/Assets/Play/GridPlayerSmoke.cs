#nullable enable
using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace AiNative.Examples.GridGame.Play
{
    public sealed class GridPlayerSmoke : MonoBehaviour
    {
        [Serializable]
        private sealed class Result
        {
            public bool success;
            public string message = "";
            public int completedTick;
            public int bundlesAfterDisconnect;
            public int scopesAfterClose;
        }

        private async UniTaskVoid Start()
        {
            string? resultPath = Argument("-playerSmokeResult");
            if (resultPath == null) { return; }
            var result = new Result();
            GridGameBootstrap? game = null;
            try
            {
                game = FindFirstObjectByType<GridGameBootstrap>();
                Check(game != null, "GridGame host is missing.");
                game!.SecondsPerEvent = 0;
                for (int frame = 0; !game.UIReady && game.UIError == null && frame < 600; frame++)
                { await UniTask.NextFrame(); }
                Check(game.UIError == null && game.UIReady, "Grid HUD did not load: " + game.UIError);
                Check(game.Session.Tick == 0, "Initial tick is not zero.");
                var app = game.UiApplication;
                Check(app.UI.TryGet<GridHud>(out var hud, out _), "Grid HUD was not registered.");
                Check(app.Assets.GetDiagnostics().Bundles.Count > 0, "Grid HUD did not load a Bundle.");

                void Tile(int x, int y) => game.HandlePointer(Camera.main.WorldToScreenPoint(new Vector3(x, y)));
                Tile(0, 0);
                Tile(1, 0);
                await Presented(game, 1);
                Tile(2, 0);
                await Presented(game, 2);
                Check(game.Frames.Presented.State.Units.Single(unit => unit.Id == 2).Health == 2,
                    "Attack did not reduce enemy health.");
                string screenshot = Path.ChangeExtension(resultPath, ".png");
                CaptureScene(screenshot);

                hud!.EndTurn.onClick.Invoke();
                await Presented(game, 3);
                result.completedTick = (int)game.Session.Tick;
                hud.Restart.onClick.Invoke();
                Check(game.Session.Tick == 0, "Restart did not create a fresh match.");
                game.enabled = false;
                for (int frame = 0; !app.Idle && frame < 600; frame++) { await UniTask.NextFrame(); }
                Check(app.Idle && app.LastError == null, "Grid HUD cleanup did not settle.");
                Check(app.UI.GetDiagnostics().WindowCount == 0, "Grid HUD remained after disconnect.");
                result.bundlesAfterDisconnect = app.Assets.GetDiagnostics().Bundles.Count;
                Check(result.bundlesAfterDisconnect == 0, "Grid HUD Bundle remained after disconnect.");
                await app.Core.CloseAsync();
                result.scopesAfterClose = app.Assets.GetDiagnostics().Scopes.Count;
                Check(result.scopesAfterClose == 0, "Grid application scope remained after close.");
                result.success = true;
                result.message = "Grid Player input, HUD, restart, screenshot and cleanup passed.";
            }
            catch (Exception error)
            {
                result.message = error.ToString();
                Debug.LogException(error);
                if (game != null)
                {
                    game.enabled = false;
                    var app = game.UiApplication;
                    for (int frame = 0; !app.Idle && frame < 600; frame++) { await UniTask.NextFrame(); }
                    try { await app.Core.CloseAsync(); }
                    catch (Exception cleanup) { Debug.LogException(cleanup); }
                }
            }
            File.WriteAllText(resultPath, JsonUtility.ToJson(result));
            Application.Quit(result.success ? 0 : 1);
        }

        private static async UniTask Presented(GridGameBootstrap game, long tick)
        {
            for (int frame = 0; game.Frames.Presented.Tick != tick && frame < 300; frame++)
            { await UniTask.NextFrame(); }
            Check(game.Session.Tick == tick && game.Frames.Presented.Tick == tick,
                "Grid tick " + tick + " was not presented.");
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
