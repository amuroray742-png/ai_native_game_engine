using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    [Serializable]
    public sealed class ResourceTypeFilter
    {
        public string AssemblyQualifiedName = "";
    }

    [Serializable]
    public sealed class ResourceFilter
    {
        public UnityEngine.Object[] ExcludedFolders = Array.Empty<UnityEngine.Object>();
        public ResourceTypeFilter[] IncludedTypes = Array.Empty<ResourceTypeFilter>();
        internal string Exclusion(string path)
        {
            foreach (var folder in ExcludedFolders)
            {
                string root = AssetDatabase.GetAssetPath(folder);
                if (!AssetDatabase.IsValidFolder(root)) { throw new AssetException("InvalidFilter", "Excluded path must be a project folder."); }
                if (path.StartsWith(root + "/", StringComparison.Ordinal) || path == root) { return "excluded-directory:" + root; }
            }
            var types = IncludedTypes.Select(x => Type.GetType(x.AssemblyQualifiedName, false)).ToArray();
            if (types.Any(x => x == null || !typeof(UnityEngine.Object).IsAssignableFrom(x)))
            { throw new AssetException("InvalidFilter", "Resource type is missing or is not a Unity object type."); }
            Type actual = AssetDatabase.GetMainAssetTypeAtPath(path);
            return types.Length != 0 && (actual == null || !types.Any(x => x.IsAssignableFrom(actual))) ? "excluded-type" : null;
        }
    }

    [Serializable]
    public sealed class PlatformResourceFilter
    {
        public BuildTarget Target = BuildTarget.StandaloneWindows64;
        public bool OverrideFolders;
        public bool OverrideTypes;
        public ResourceFilter Filter = new ResourceFilter();
    }

    [CustomPropertyDrawer(typeof(ResourceTypeFilter))]
    internal sealed class ResourceTypeFilterDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var name = property.FindPropertyRelative("AssemblyQualifiedName");
            var type = Type.GetType(name.stringValue, false);
            if (!EditorGUI.DropdownButton(position, new GUIContent(type?.FullName ?? "Select resource type"), FocusType.Keyboard)) { return; }
            var menu = new GenericMenu();
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<UnityEngine.Object>().Where(x => !x.IsGenericType && x.IsPublic).OrderBy(x => x.FullName))
            {
                var selected = candidate;
                menu.AddItem(new GUIContent(selected.FullName.Replace('.', '/')), selected == type, () =>
                { name.stringValue = selected.AssemblyQualifiedName; name.serializedObject.ApplyModifiedProperties(); });
            }
            menu.ShowAsContext();
        }
    }
}
