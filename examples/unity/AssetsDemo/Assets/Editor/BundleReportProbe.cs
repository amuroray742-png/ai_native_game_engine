using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BundleReportProbe
{
    public static void Run()
    {
        const string root = "Assets/Probe";
        Directory.CreateDirectory(root);
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(root + "/SharedTexture.asset");
        if (texture == null)
        {
        texture = new Texture2D(2, 2);
        texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
        texture.Apply();
        AssetDatabase.CreateAsset(texture, root + "/SharedTexture.asset");
        }
        var material = AssetDatabase.LoadAssetAtPath<Material>(root + "/SharedMaterial.mat");
        if (material == null)
        {
        material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { mainTexture = texture };
        AssetDatabase.CreateAsset(material, root + "/SharedMaterial.mat");
        }
        foreach (string name in new[] { "A", "B" })
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(root + "/" + name + ".prefab") != null) { continue; }
            var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.GetComponent<Renderer>().sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(obj, root + "/" + name + ".prefab");
            UnityEngine.Object.DestroyImmediate(obj);
        }
        AssetDatabase.SaveAssets();
        string output = Path.GetFullPath("../../../TestResults/assets-probe/bundles");
        Directory.CreateDirectory(output);
        var definitions = new[] {
            new AssetBundleBuild { assetBundleName = "a", assetNames = new[] { root + "/A.prefab" } },
            new AssetBundleBuild { assetBundleName = "b", assetNames = new[] { root + "/B.prefab" } },
            new AssetBundleBuild { assetBundleName = "shared", assetNames = new[] { root + "/SharedMaterial.mat", root + "/SharedTexture.asset" } }
        };
        var manifest = BuildPipeline.BuildAssetBundles(output, definitions, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
        if (manifest == null) { throw new Exception("Probe build failed"); }
        var report = BuildReport.GetLatestReport();
        var lines = report.packedAssets.SelectMany(p => p.contents.Select(c => p.shortPath + "\t" + c.sourceAssetGUID + "\t" + c.sourceAssetPath));
        File.WriteAllLines(Path.Combine(output, "packed-assets.tsv"), lines);
        File.Copy("Library/LastBuild.buildreport", Path.Combine(output, "probe.buildreport"), true);
        Debug.Log("ASSETS_PROBE " + output + "\n" + string.Join("\n", lines));
    }
}
