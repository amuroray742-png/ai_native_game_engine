using System;
using System.IO;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class AssetsDemoBootstrap : MonoBehaviour
{
    private string _status = "Starting";
    private bool _continue;
    private bool _showContinue;
    private async UniTaskVoid Start()
    {
        Application.runInBackground = true;
        string renderingError = null;
        void ObserveLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            { renderingError ??= message; }
        }
        Application.logMessageReceived += ObserveLog;
        try
        {
#if UNITY_EDITOR
            var groups = UnityEditor.AssetDatabase.LoadAssetAtPath<AiNative.UnityCore.Assets.Editor.AssetGroups>("Assets/ResourceGroups.asset");
            var module = AiNative.UnityCore.Assets.Editor.EditorAssets.CreateConfigured(groups);
#else
            var module = new AssetsModule();
#endif
            Core.Inst.RegisterModule(module);
            await Core.Inst.RunAsync();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsShaderProbe") >= 0) { await ShaderProbeVerification.Run(module); }
            var first = await module.InstantiateAsync("Assets/Probe/A");
            var second = await module.InstantiateAsync("Assets/Probe/B");
            first.Value.transform.position = Vector3.left;
            second.Value.transform.position = Vector3.right;
            var material = first.Value.GetComponent<Renderer>().sharedMaterial;
            if (material != second.Value.GetComponent<Renderer>().sharedMaterial) { throw new Exception("Shared material identity differs."); }
            if (material.mainTexture != second.Value.GetComponent<Renderer>().sharedMaterial.mainTexture) { throw new Exception("Shared texture identity differs."); }
            _status = "A and B share one material and texture. Continue to test pool, scene and cleanup.";
            Capture();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsSmoke") < 0)
            {
                _showContinue = true;
                await UniTask.WaitUntil(() => _continue, cancellationToken: this.GetCancellationTokenOnDestroy());
                _showContinue = false;
            }
            first.Dispose();
            await UniTask.Yield();
            if (second.Value.GetComponent<Renderer>().sharedMaterial != material) { throw new Exception("Releasing A destroyed B dependencies."); }
            var data = await module.LoadAsync<ScriptableObject>("Assets/Probe/Data");
            if (data.Value.GetType().Name != "DemoData" || !Equals(data.Value.GetType().GetField("Value")?.GetValue(data.Value), 42)) { throw new Exception("Bundle script type failed."); }
            data.Dispose();
            var pool = module.CreatePool("Assets/Probe/A", new AssetPoolOptions(2));
            await pool.PrewarmAsync(2);
            var rental = await pool.RentAsync();
            rental.Dispose();
            await pool.CloseAsync();
            var scene = await module.LoadSceneAsync("Assets/Probe/Content");
            var sceneRenderer = scene.Value.GetRootGameObjects()[0].GetComponentInChildren<Renderer>();
            if (sceneRenderer.sharedMaterial != material) { throw new Exception("Scene external material identity differs."); }
            await scene.UnloadAsync();
            second.Dispose();
            await Core.Inst.RestartAsync();
            using (var again = await module.LoadAsync<GameObject>("Assets/Probe/A")) { if (again.Value == null) { throw new Exception("Restart failed."); } }
            await Core.Inst.CloseAsync();
            var diagnostics = module.GetDiagnostics();
            if (diagnostics.Bundles.Count != 0 || diagnostics.PendingOperations != 0 || diagnostics.Scopes.Count != 0) { throw new Exception("Resources remain after shutdown."); }
            _status = "PASS: shared resources, pool, scene, script types, restart and cleanup";
            if (renderingError != null) { throw new Exception("Player logged an error during acceptance: " + renderingError); }
            Debug.Log(_status);
            WriteResult(true, _status);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsSmoke") >= 0) { Application.Quit(0); }
        }
        catch (Exception error)
        {
            _status = error.ToString();
            Debug.LogException(error);
            WriteResult(false, _status);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsSmoke") >= 0) { Application.Quit(1); }
        }
        finally { Application.logMessageReceived -= ObserveLog; }
    }
    private static void Capture()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-assetsResult");
        if (index < 0 || index + 1 >= args.Length) { return; }
        var camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
        var target = RenderTexture.GetTemporary(640, 360, 24);
        var previous = RenderTexture.active;
        var texture = new Texture2D(640, 360, TextureFormat.RGB24, false);
        try
        {
            camera.targetTexture = target;
            if (GraphicsSettings.currentRenderPipeline == null) { camera.Render(); }
            else { RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target }); }
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
            texture.Apply();
            File.WriteAllBytes(Path.ChangeExtension(args[index + 1], ".png"), texture.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(texture);
        }
    }
    private static void WriteResult(bool success, string message)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-assetsResult");
        if (index >= 0 && index + 1 < args.Length) { File.WriteAllText(args[index + 1], JsonUtility.ToJson(new SmokeResult { success = success, message = message })); }
    }
    [Serializable] private sealed class SmokeResult { public bool success; public string message; }
    private void OnGUI()
    {
        GUI.Label(new Rect(20, 20, Screen.width - 40, 80), _status);
        if (_showContinue && GUI.Button(new Rect(20, 110, 200, 40), "Run cleanup checks")) { _continue = true; }
    }
}
