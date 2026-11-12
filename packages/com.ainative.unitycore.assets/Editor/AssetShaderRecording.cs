using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    /// <summary>Explicit Editor recording session, persisted through Play Mode domain reloads.</summary>
    public static class AssetShaderRecording
    {
        private const string SessionKey = "AiNative.Assets.ShaderRecording.SettingsGuid";
        public static bool IsRecording => !string.IsNullOrEmpty(SessionState.GetString(SessionKey, ""));

        public static void Begin(AssetShaderSettings settings)
        {
            if (IsRecording) { throw new InvalidOperationException("A shader recording is already active. Save or cancel it first."); }
            string path = AssetDatabase.GetAssetPath(settings);
            if (string.IsNullOrEmpty(path)) { throw new ArgumentException("Save Shader Settings as an asset before recording."); }
            // Resolve both operations before clearing the Editor-wide recording.
            Find("SaveCurrentShaderVariantCollection", typeof(string));
            Find("ClearCurrentShaderVariantCollection").Invoke(null, null);
            SessionState.SetString(SessionKey, AssetDatabase.AssetPathToGUID(path));
        }

        public static ShaderVariantCollection Save(string assetPath)
        {
            if (!IsRecording) { throw new InvalidOperationException("Begin a shader recording first."); }
            AssetShaderAutoCollection.ValidateNewPath(assetPath);
            string guid = SessionState.GetString(SessionKey, "");
            var settings = AssetDatabase.LoadAssetAtPath<AssetShaderSettings>(AssetDatabase.GUIDToAssetPath(guid));
            if (settings == null) { throw new InvalidOperationException("The recording's Shader Settings asset was removed. Cancel this session."); }
            Find("SaveCurrentShaderVariantCollection", typeof(string)).Invoke(null, new object[] { assetPath });
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(assetPath);
            if (collection == null) { throw new InvalidOperationException("Unity did not save a shader recording: " + assetPath); }
            Undo.RecordObject(settings, "Attach shader recording");
            settings.RuntimeCollections = settings.RuntimeCollections.Concat(new[] { collection }).ToArray();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            SessionState.EraseString(SessionKey);
            Debug.Log("Recorded " + collection.variantCount + " variants. Attached to " + AssetDatabase.GetAssetPath(settings));
            return collection;
        }

        public static void Cancel()
        {
            SessionState.EraseString(SessionKey);
        }

        private static MethodInfo Find(string name, params Type[] parameters)
        {
            // Unity 6000.3 exposes its Graphics Settings recorder only through internal Editor APIs.
            // Keep the version-sensitive bridge here; never fall back to an empty successful capture.
            return typeof(ShaderUtil).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic,
                null, parameters, null) ?? throw new NotSupportedException("Shader recording API " + name +
                    " is unavailable in Unity " + Application.unityVersion);
        }
    }
}
