using System;
using System.IO;
using System.Linq;
using System.Threading;
using AiNative.UnityCore;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class ShaderVariantDemo : MonoBehaviour
{
    public int ContentVersion;
    private AssetsModule _module;
    private Core _core;
    private AssetScope _scope;
    private Material _keyword;
    private Camera _camera;
    private string _source;
    private string _status = "Loading bundle content...";
    private bool _ready;
    private bool _green;
    private bool _checkingGreen;
    private UniTask? _closeTask;
    private int _missingErrors;
    private string _unexpectedError;
    public bool Ready => _ready;
    public bool Green => _green;
    public string Source => _source;

    private async UniTaskVoid Start()
    {
        Application.runInBackground = true;
        Application.logMessageReceived += ObserveLog;
        try
        {
            _source = Path.Combine(Application.streamingAssetsPath, "AiNativeAssets");
#if UNITY_EDITOR
            var groups = UnityEditor.AssetDatabase.LoadAssetAtPath<AiNative.UnityCore.Assets.Editor.AssetGroups>("Assets/ShaderVariantDemo/Groups.asset");
            if (groups == null || string.IsNullOrEmpty(groups.LastBuildPath))
            { throw new InvalidOperationException("Build Shader Variant Demo bundles first. There is no AssetDatabase fallback."); }
            _source = Path.GetFullPath(groups.LastBuildPath);
            ContentVersion = groups.LastBuildVersion;
#endif
            _module = new AssetsModule(_source);
            _core = Core.Inst;
            _core.RegisterModule(_module);
            await _core.RunAsync(this.GetCancellationTokenOnDestroy());
            _scope = _module.CreateScope("shader-variant-demo");
            var token = this.GetCancellationTokenOnDestroy();
            await _scope.WarmupShadersAsync(new[] { "ShaderDemo/Variants" }, token);
            var instance = await _scope.InstantiateAsync("ShaderDemo/ShaderProbe", cancellationToken: token);
            var renderer = instance.Value.GetComponentsInChildren<Renderer>().Single(x => x.name == "Keyword");
            _keyword = new Material(renderer.sharedMaterial);
            renderer.sharedMaterial = _keyword;
            _camera = GetComponentInChildren<Camera>();
            _ready = true;
            SetGreen(true);
            _status = "Keyword | Lit | Unlit | Graph | GraphEmission";
#if UNITY_EDITOR
            _status += "\nEditor preview: use the strict Windows Player to verify missing variants.";
#endif
            string expectation = Argument("-variantExpect");
            if (expectation != null) { await Verify(expectation, token); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _status = error.ToString();
            Debug.LogException(error);
            await CloseAsync();
            _status = error.ToString();
            if (Argument("-variantResult") != null)
            {
                WriteResult(false, error.ToString());
                Application.Quit(1);
            }
        }
    }

    public void SetGreen(bool enabled)
    {
        if (!_ready) { return; }
        _green = enabled;
        if (enabled) { _keyword.EnableKeyword("PROBE_GREEN"); }
        else { _keyword.DisableKeyword("PROBE_GREEN"); }
    }

    private void ObserveLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
        bool target = message == "Shader AI Native/Keyword Probe, subshader 0, pass 0, stage vertex: variant PROBE_GREEN not found." ||
            message == "Shader AI Native/Keyword Probe, subshader 0, pass 0, stage pixel: variant PROBE_GREEN not found.";
        if (_checkingGreen && Argument("-variantExpect") == "missing" && target) { _missingErrors++; }
        else { _unexpectedError ??= message; }
    }

    private async UniTask Verify(string expectation, CancellationToken token)
    {
        if (expectation != "present" && expectation != "missing") { throw new ArgumentException("Expected present or missing."); }
        // Do not render the startup green request outside the acceptance window.
        SetGreen(false);
        await UniTask.NextFrame(cancellationToken: token);
        Capture("red", false);
        _checkingGreen = true;
        SetGreen(true);
        await UniTask.NextFrame(cancellationToken: token);
        Capture(expectation == "missing" ? "missing" : "green", expectation == "missing");
        // Stop further automatic camera draws before closing the expected-error window.
        _camera.enabled = false;
        _checkingGreen = false;
        if (expectation == "missing" && _missingErrors == 0) { throw new Exception("No matching missing-variant error was observed."); }
        if (_unexpectedError != null) { throw new Exception("Unexpected rendering error: " + _unexpectedError); }
        await CloseAsync();
        if (_unexpectedError != null) { throw new Exception("Error during shutdown: " + _unexpectedError); }
        var state = _module.GetDiagnostics();
        if (state.PendingOperations != 0 || state.Bundles.Count != 0 || state.Scopes.Count != 0)
        { throw new Exception("Demo resources remain after close."); }
        WriteResult(true, "Pixel, expected-error and cleanup checks passed.");
        Application.Quit(0);
    }

    private void Capture(string name, bool missing)
    {
        var target = RenderTexture.GetTemporary(256, 128, 24);
        var previous = RenderTexture.active;
        var pixels = new Texture2D(256, 128, TextureFormat.RGB24, false);
        try
        {
            RenderPipeline.SubmitRenderRequest(_camera, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 256, 128), 0, 0);
            pixels.Apply();
            string result = Argument("-variantResult");
            File.WriteAllBytes(Path.ChangeExtension(result, name + ".png"), pixels.EncodeToPNG());
            Color value = pixels.GetPixel(32, 64);
            bool valid = missing ? value.r > .7f && value.b > .7f && value.g < .3f :
                _green ? value.g > .7f && value.r < .2f && value.b < .2f : value.r > .7f && value.g < .2f && value.b < .2f;
            if (!valid) { throw new Exception(name + " pixel mismatch: " + value); }
            foreach (int x in new[] { 80, 128, 176, 224 })
            {
                value = pixels.GetPixel(x, 64);
                if (value.maxColorComponent < .05f || (value.r > .8f && value.b > .8f && value.g < .2f))
                { throw new Exception("Control material pixel mismatch at " + x + ": " + value); }
            }
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Destroy(pixels);
        }
    }

    public UniTask CloseAsync()
    {
        if (!_closeTask.HasValue) { _closeTask = CloseOperationAsync().Preserve(); }
        return _closeTask.Value;
    }

    private async UniTask CloseOperationAsync()
    {
        _ready = false;
        if (_keyword != null) { Destroy(_keyword); _keyword = null; }
        if (_scope != null) { await _scope.CloseAsync(); }
        if (_module != null) { await _core.CloseAsync(); }
        _status = "Closed: demo resources released.";
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= ObserveLog;
        if (_keyword != null) { Destroy(_keyword); }
        // Core's host handles Play exit; invalidate this scene's scope immediately on destruction.
        if (_module != null) { _module.Shut(null); }
        if (_core != null) { _core.CloseAsync().Forget(Debug.LogException); }
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(20, 15, Screen.width - 40, 80), "Shader variant demo — content version " + ContentVersion + "\nBundle source: " + _source + "\n" + _status);
        GUI.enabled = _ready;
        if (GUI.Button(new Rect(20, 100, 180, 35), "Base variant (red)")) { SetGreen(false); }
        if (GUI.Button(new Rect(210, 100, 220, 35), "PROBE_GREEN (green / error)")) { SetGreen(true); }
        if (GUI.Button(new Rect(440, 100, 150, 35), "Close demo")) { CloseAsync().Forget(); }
        GUI.enabled = true;
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    [Serializable] private sealed class Result
    {
        public bool success; public string message; public int contentVersion; public string source;
        public int expectedMissingErrors;
    }
    private void WriteResult(bool success, string message)
    {
        string path = Argument("-variantResult");
        if (path != null) { File.WriteAllText(path, JsonUtility.ToJson(new Result { success = success, message = message,
            contentVersion = ContentVersion, source = _source, expectedMissingErrors = _missingErrors }, true)); }
    }
}
