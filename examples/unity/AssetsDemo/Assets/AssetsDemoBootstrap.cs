using System;
using System.IO;
using System.Threading;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class AssetsDemoBootstrap : MonoBehaviour
{
    private string _status = "Starting";
    private bool _continue;
    [SerializeField] private UnityEngine.UI.Text sceneStatus;
    private DemoControls _controls;
    private Core _core;
    private AssetsModule _module;
    private bool _destroyed;
    private async UniTaskVoid Start()
    {
        Application.runInBackground = true;
        CancellationToken destruction = this.GetCancellationTokenOnDestroy();
        bool smoke = Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsSmoke") >= 0;
        bool expectedDestroy = Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsDestroyAfterFirstLoad") >= 0;
        Exception failure = null;
        string completed = null;
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
            _module = AiNative.UnityCore.Assets.Editor.EditorAssets.CreateConfigured(groups);
#else
            _module = new AssetsModule();
#endif
            var core = Core.Inst;
            core.RegisterModule(_module);
            _core = core;
            _controls = new DemoControls(transform, "Assets/DemoControls/AssetsPanel", () => _continue = true);
            core.RegisterModule(_controls.Module);
            await _core.RunAsync(destruction);
            await _controls.OpenAsync(destruction);
            _controls.Show(_status, false);
            if (sceneStatus != null) { sceneStatus.enabled = false; }
            FailAt("after-start");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-assetsShaderProbe") >= 0)
            { await ShaderProbeVerification.Run(_module, destruction); }
            var first = await _module.InstantiateAsync("Assets/Probe/A", cancellationToken: destruction);
            FailAt("after-first-load");
            if (expectedDestroy)
            {
                Destroy(gameObject);
                await UniTask.WaitUntil(() => false, cancellationToken: destruction);
            }
            var second = await _module.InstantiateAsync("Assets/Probe/B", cancellationToken: destruction);
            first.Value.transform.position = Vector3.left;
            second.Value.transform.position = Vector3.right;
            var material = first.Value.GetComponent<Renderer>().sharedMaterial;
            if (material != second.Value.GetComponent<Renderer>().sharedMaterial) { throw new Exception("Shared material identity differs."); }
            if (material.mainTexture != second.Value.GetComponent<Renderer>().sharedMaterial.mainTexture) { throw new Exception("Shared texture identity differs."); }
            _status = "A and B share one material and texture. Continue to test pool, scene and cleanup.";
            _controls.Show(_status, true);
            Capture();
            if (smoke) { _controls.ClickForVerification(0); }
            await UniTask.WaitUntil(() => _continue, cancellationToken: destruction);
            _controls.Show("Running cleanup checks...", false);
            first.Dispose();
            await UniTask.Yield();
            if (second.Value.GetComponent<Renderer>().sharedMaterial != material) { throw new Exception("Releasing A destroyed B dependencies."); }
            var data = await _module.LoadAsync<ScriptableObject>("Assets/Probe/Data", destruction);
            if (data.Value.GetType().Name != "DemoData" || !Equals(data.Value.GetType().GetField("Value")?.GetValue(data.Value), 42)) { throw new Exception("Bundle script type failed."); }
            data.Dispose();
            var pool = _module.CreatePool("Assets/Probe/A", new AssetPoolOptions(2));
            await pool.PrewarmAsync(2, destruction);
            var rental = await pool.RentAsync(cancellationToken: destruction);
            rental.Dispose();
            await pool.CloseAsync();
            var scene = await _module.LoadSceneAsync("Assets/Probe/Content", destruction);
            var sceneRenderer = scene.Value.GetRootGameObjects()[0].GetComponentInChildren<Renderer>();
            if (sceneRenderer.sharedMaterial != material) { throw new Exception("Scene external material identity differs."); }
            await scene.UnloadAsync();
            second.Dispose();
            await _core.RestartAsync(destruction);
            using (var again = await _module.LoadAsync<GameObject>("Assets/Probe/A", destruction)) { if (again.Value == null) { throw new Exception("Restart failed."); } }
            completed = "PASS: shared resources, pool, scene, script types, restart and cleanup";
        }
        catch (OperationCanceledException) when (_destroyed && expectedDestroy)
        { completed = "PASS: scene destruction cancelled work and released resources"; }
        catch (OperationCanceledException) when (_destroyed) { }
        catch (Exception error) { failure = error; }
        finally
        {
            if (_core != null)
            {
                try { await _core.CloseAsync(); }
                catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
                finally { _core = null; }
            }
            if (_module != null)
            {
                var diagnostics = _module.GetDiagnostics();
                if (diagnostics.Bundles.Count != 0 || diagnostics.PendingOperations != 0 || diagnostics.Scopes.Count != 0)
                { failure = new InvalidOperationException("Resources remain after shutdown.", failure); }
            }
            Application.logMessageReceived -= ObserveLog;
        }
        if (renderingError != null)
        { failure = new InvalidOperationException("Player logged an error during acceptance: " + renderingError, failure); }
        if (_destroyed && !expectedDestroy && failure == null) { return; }
        _status = failure == null ? completed : failure.ToString();
        if (sceneStatus != null) { sceneStatus.text = _status; sceneStatus.enabled = true; }
        if (failure == null) { Debug.Log(_status); }
        else { Debug.LogException(failure); }
        WriteResult(failure == null, _status);
        if (smoke) { Application.Quit(failure == null ? 0 : 1); }
    }
    private static void FailAt(string stage)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-assetsFailAt");
        if (index >= 0 && index + 1 < args.Length && args[index + 1] == stage)
        { throw new InvalidOperationException("Injected AssetsDemo failure at " + stage); }
    }
    private void OnDestroy()
    {
        _destroyed = true;
        if (_core != null) { _core.CloseAsync().Forget(Debug.LogException); }
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
}
