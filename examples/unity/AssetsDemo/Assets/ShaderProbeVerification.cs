using System;
using System.IO;
using System.Linq;
using AiNative.UnityCore.Assets;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

public static class ShaderProbeVerification
{
    public static async UniTask Run(IAssets assets)
    {
        var scope = assets.CreateScope("shader-verification");
        GameObject cameraObject = null;
        RenderTexture target = null;
        Texture2D pixels = null;
        Material keyword = null;
        Material graph = null;
        try
        {
            await scope.WarmupShadersAsync(new[] { "Assets/Rendering/ProbeVariants" });
            var instance = await scope.InstantiateAsync("Assets/Rendering/ShaderProbe");
            instance.Value.transform.position = new Vector3(0, 0, 100);
            cameraObject = new GameObject("Shader verification camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, 90);
            camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 2;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            target = new RenderTexture(256, 128, 24);
            target.Create();
            camera.targetTexture = target;
            pixels = new Texture2D(256, 128, TextureFormat.RGB24, false);
            var renderers = instance.Value.GetComponentsInChildren<Renderer>();
            var probe = renderers.Single(x => x.name == "Keyword");
            keyword = new Material(probe.sharedMaterial);
            probe.sharedMaterial = keyword;
            var graphRenderer = renderers.Single(x => x.name == "Graph");
            graph = new Material(graphRenderer.sharedMaterial);
            // Leave headroom for the emission assertion even under the probe's two lights.
            if (graph.HasProperty("_BaseColor")) { graph.SetColor("_BaseColor", new Color(.03f, .03f, .03f, 1)); }
            if (graph.HasProperty("_EmissionColor")) { graph.SetColor("_EmissionColor", Color.green * 2); }
            graph.DisableKeyword("_EMISSION");
            graphRenderer.sharedMaterial = graph;
            foreach (var renderer in renderers)
            {
                if (!renderer.sharedMaterial.shader.isSupported) { throw new Exception("Unsupported shader: " + renderer.sharedMaterial.shader.name); }
            }
            await UniTask.NextFrame();
            Capture(camera, target, pixels);
            Color red = pixels.GetPixel(32, 64);
            Color graphBefore = pixels.GetPixel(176, 64);
            if (red.r < .7f || red.g > .2f) { throw new Exception("Keyword baseline did not render red: " + red); }
            foreach (int x in new[] { 80, 128, 176, 224 })
            {
                Color value = pixels.GetPixel(x, 64);
                if (value.maxColorComponent < .05f || (value.r > .8f && value.b > .8f && value.g < .2f))
                { throw new Exception("Lit/Unlit/Graph shader pixel invalid at " + x + ": " + value); }
            }
            keyword.EnableKeyword("PROBE_GREEN");
            graph.EnableKeyword("_EMISSION");
            if (graph.HasProperty("_EmissionColor")) { graph.SetColor("_EmissionColor", Color.green * 2); }
            await UniTask.NextFrame();
            Capture(camera, target, pixels);
            Color green = pixels.GetPixel(32, 64);
            Color graphAfter = pixels.GetPixel(176, 64);
            if (green.g < .7f || green.r > .2f) { throw new Exception("Runtime keyword did not render green: " + green); }
            // The template keyword selects the emission-map branch (black default)
            // instead of the constant emission color, so switching reduces green.
            if (graphBefore.g < graphAfter.g + .2f) { throw new Exception("Shader Graph emission-map keyword did not change the rendered pixel: " + graphBefore + " -> " + graphAfter); }
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-assetsResult");
            if (index >= 0 && index + 1 < args.Length)
            { File.WriteAllBytes(Path.ChangeExtension(args[index + 1], "shader.png"), pixels.EncodeToPNG()); }
            Debug.Log("PASS: URP Lit, Unlit, Shader Graph, runtime keywords and collection warmup rendered from real bundles.");
        }
        finally
        {
            if (cameraObject != null) { UnityEngine.Object.Destroy(cameraObject); }
            if (keyword != null) { UnityEngine.Object.Destroy(keyword); }
            if (graph != null) { UnityEngine.Object.Destroy(graph); }
            if (pixels != null) { UnityEngine.Object.Destroy(pixels); }
            if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            await scope.CloseAsync();
        }
    }
    private static void Capture(Camera camera, RenderTexture target, Texture2D pixels)
    {
        var previous = RenderTexture.active;
        try
        {
            RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            pixels.Apply();
        }
        finally { RenderTexture.active = previous; }
    }
}
