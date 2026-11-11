using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AiNative.UnityCore.Assets.Editor
{
    public static class AssetDiagnosticsExport
    {
        [Serializable] private sealed class Owner
        { public long Id; public string Kind; public string Name; public long OwnerId; public string ResourceKey; public string Bundle; public string State; public string[] Dependencies; }
        [Serializable] private sealed class Operation
        { public long Id; public string Name; public string State; public double QueueMilliseconds; public double LoadMilliseconds; }
        [Serializable] private sealed class Peak { public string Kind; public int Count; }
        [Serializable] private sealed class Snapshot
        {
            public int FormatVersion = 1;
            public string Generation;
            public Owner[] Owners;
            public Operation[] Operations;
            public Peak[] Peaks;
            public long SharedHits;
            public long FailedOperations;
            public long CancelledOperations;
            public int ActiveLoads;
            public int QueuedLoads;
            public int PeakLoads;
            public string[] ResourceMeasurements;
            public string[] Errors;
        }
        public static void Write(AssetDiagnostics diagnostics, string path)
        {
            var source = diagnostics.Ownership;
            if (source == null) { throw new InvalidOperationException("The module has not initialized."); }
            var snapshot = new Snapshot
            {
                Generation = source.Generation,
                Owners = source.Owners.Select(x => new Owner { Id = x.Id, Kind = x.Kind, Name = x.Name, OwnerId = x.OwnerId,
                    ResourceKey = x.ResourceKey, Bundle = x.Bundle, State = x.State, Dependencies = x.Dependencies.ToArray() }).ToArray(),
                Operations = source.RecentOperations.Select(x => new Operation { Id = x.Id, Name = x.Name, State = x.State,
                    QueueMilliseconds = x.QueueMilliseconds, LoadMilliseconds = x.LoadMilliseconds }).ToArray(),
                Peaks = source.Peaks.Select(x => new Peak { Kind = x.Key, Count = x.Value }).ToArray(),
                SharedHits = source.SharedHits, FailedOperations = source.Failures, CancelledOperations = source.Cancellations,
                ActiveLoads = source.ActiveLoads, QueuedLoads = source.QueuedLoads, PeakLoads = source.PeakLoads,
                ResourceMeasurements = diagnostics.Resources.ToArray(), Errors = diagnostics.Errors.ToArray()
            };
            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
        }
        public static string Difference(AssetOwnershipSnapshot before, AssetOwnershipSnapshot after)
        {
            if (before.Generation != after.Generation) { return "Different runtime generations; owner IDs cannot be compared."; }
            var previous = before.Owners.ToDictionary(x => x.Id);
            var current = after.Owners.ToDictionary(x => x.Id);
            return "Added: " + string.Join(", ", current.Keys.Except(previous.Keys)) +
                "\nReleased: " + string.Join(", ", previous.Keys.Except(current.Keys)) +
                "\nChanged: " + string.Join(", ", current.Values.Where(x => previous.TryGetValue(x.Id, out var old) &&
                    (old.State != x.State || old.OwnerId != x.OwnerId)).Select(x => x.Id));
        }
    }

    internal sealed class AssetAnalysisWindow : EditorWindow
    {
        private string _text;
        private Vector2 _scroll;
        internal static void OpenComparison()
        {
            string before = EditorUtility.OpenFilePanel("Previous build-analysis.json", "", "json");
            if (string.IsNullOrEmpty(before)) { return; }
            string after = EditorUtility.OpenFilePanel("Current build-analysis.json", "", "json");
            if (string.IsNullOrEmpty(after)) { return; }
            var report = AssetBuildAnalysis.Read(after);
            GetWindow<AssetAnalysisWindow>("Build Analysis")._text = string.Join("\n", AssetBuildAnalysis.Compare(AssetBuildAnalysis.Read(before), report)) +
                "\n\nDependency file costs (not memory)\n" + string.Join("\n", report.Sources.Where(x => !string.IsNullOrEmpty(x.Address))
                    .OrderByDescending(x => x.DependencyFileBytes).Select(x => x.Address + "\t" + x.DependencyFileBytes)) +
                "\n\nCollection decisions\n" + string.Join("\n", report.CollectionDecisions ?? Array.Empty<string>());
        }
        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(_text ?? "Select two build reports.");
            EditorGUILayout.EndScrollView();
        }
    }
}
