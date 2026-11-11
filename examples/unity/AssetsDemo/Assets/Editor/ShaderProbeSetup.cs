using System;
using System.Linq;
using AiNative.UnityCore.Assets.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ShaderProbeSetup
{
    private const string Root = "Assets/Rendering";
    public static void Create()
    {
        const string graph = Root + "/ProbeGraph.shadergraph";
        if (AssetDatabase.LoadAssetAtPath<Shader>(graph) == null)
        {
            if (!AssetDatabase.CopyAsset("Packages/com.unity.shadergraph/GraphTemplates/Cross Pipeline/1_Lit Full.shadergraph", graph))
            { throw new InvalidOperationException("Cannot copy the installed Shader Graph template."); }
            AssetDatabase.ImportAsset(graph, ImportAssetOptions.ForceSynchronousImport);
        }
        var graphShader = AssetDatabase.LoadAssetAtPath<Shader>(graph);
        if (graphShader == null) { throw new InvalidOperationException("Shader Graph did not import as a shader."); }
        var materials = new[]
        {
            Material("Keyword", Shader.Find("AI Native/Keyword Probe")),
            Material("Lit", Shader.Find("Universal Render Pipeline/Lit")),
            Material("Unlit", Shader.Find("Universal Render Pipeline/Unlit")),
            Material("Graph", graphShader), Material("GraphEmission", graphShader)
        };
        materials[4].EnableKeyword("_EMISSION");
        if (materials[4].HasProperty("_EmissionColor")) { materials[4].SetColor("_EmissionColor", Color.green); }
        foreach (var material in materials) { EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material); }
        var root = new GameObject("ShaderProbe");
        try
        {
            for (int i = 0; i < materials.Length; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = materials[i].name;
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(i * 1.5f - 3, 0, 0);
                cube.GetComponent<Renderer>().sharedMaterial = materials[i];
            }
            var main = new GameObject("Main light");
            main.transform.SetParent(root.transform, false);
            main.transform.rotation = Quaternion.Euler(30, -30, 0);
            var light = main.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1; light.shadows = LightShadows.Hard;
            var additional = new GameObject("Additional light");
            additional.transform.SetParent(root.transform, false);
            additional.transform.localPosition = new Vector3(1, 1, -2);
            var point = additional.AddComponent<Light>(); point.type = LightType.Point; point.range = 10; point.intensity = 3; point.shadows = LightShadows.Hard;
            PrefabUtility.SaveAsPrefabAsset(root, Root + "/ShaderProbe.prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        var settings = AssetDatabase.LoadAssetAtPath<AssetShaderSettings>(Root + "/ShaderSettings.asset");
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<AssetShaderSettings>();
            settings.EnableProcessing = true;
            var shader = materials[0].shader;
            settings.RequiredVariants = new[]
            {
                new AssetShaderVariant { Shader = shader, PassType = PassType.ScriptableRenderPipelineDefaultUnlit, PassName = "Forward" },
                new AssetShaderVariant { Shader = shader, PassType = PassType.ScriptableRenderPipelineDefaultUnlit, PassName = "Forward", Keywords = new[] { "PROBE_GREEN" } }
            };
            settings.Exclusions = new[] { new AssetShaderExclusion { Shader = shader, PassName = "Forward", RequiredKeywords = new[] { "PROBE_EXCLUDED" } } };
            AssetDatabase.CreateAsset(settings, Root + "/ShaderSettings.asset");
        }
        if (AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(Root + "/ProbeVariants.shadervariants") == null)
        { AssetShaderCollection.Generate(settings, Root + "/ProbeVariants.shadervariants"); }
        AssetDatabase.SaveAssets();
    }

    public static void Include(AssetGroups groups)
    {
        groups.Shaders = AssetDatabase.LoadAssetAtPath<AssetShaderSettings>(Root + "/ShaderSettings.asset");
        if (groups.Shaders == null) { throw new InvalidOperationException("Run ShaderProbeSetup.Create before building the demo."); }
        groups.Groups = groups.Groups.Concat(new[] { new ResourceGroup
        {
            Id = "ShaderProbe", Assets = new[]
            {
                new ResourceAddress { Asset = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/ShaderProbe.prefab") },
                new ResourceAddress { Asset = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(Root + "/ProbeVariants.shadervariants") }
            }
        } }).ToArray();
    }
    private static Material Material(string name, Shader shader)
    {
        string path = Root + "/" + name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) { return material; }
        if (shader == null) { throw new InvalidOperationException("Missing shader for " + name); }
        material = new Material(shader) { name = name };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
