using System;
using System.Collections.Generic;
using System.Linq;

namespace AiNative.UnityCore.Assets.Editor
{
    internal static class BundleDependencyGraph
    {
        internal static Dictionary<string, HashSet<string>> Closure(Dictionary<string, HashSet<string>> graph)
        {
            var closure = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var chain = new List<string>();
            HashSet<string> Visit(string name)
            {
                if (!graph.ContainsKey(name)) { throw new AssetException("DependencyEvidenceMismatch", "Unknown bundle: " + name); }
                if (chain.Contains(name)) { throw new AssetException("CyclicBundles", string.Join(" -> ", chain.Concat(new[] { name }))); }
                if (closure.TryGetValue(name, out var known)) { return known; }
                chain.Add(name);
                var reachable = new HashSet<string>(StringComparer.Ordinal);
                foreach (string child in graph[name].OrderBy(x => x, StringComparer.Ordinal))
                {
                    reachable.Add(child);
                    reachable.UnionWith(Visit(child));
                }
                chain.RemoveAt(chain.Count - 1);
                closure.Add(name, reachable);
                return reachable;
            }
            foreach (string name in graph.Keys) { Visit(name); }
            return closure;
        }

        internal static Dictionary<string, HashSet<string>> VerifyAndReduce(AssetBuildPlan plan, Dictionary<string, HashSet<string>> actual)
        {
            if (!new HashSet<string>(plan.Dependencies.Keys).SetEquals(actual.Keys))
            { throw new AssetException("UnexpectedBundles", "Actual bundle set differs from plan."); }
            var expectedClosure = Closure(plan.Dependencies);
            var actualClosure = Closure(actual);
            var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var pair in actualClosure)
            {
                if (!expectedClosure[pair.Key].SetEquals(pair.Value))
                { throw new AssetException("DependencyEvidenceMismatch", "Actual reachability differs from plan: " + pair.Key); }
                var children = new HashSet<string>(pair.Value, StringComparer.Ordinal);
                foreach (string child in pair.Value) { children.ExceptWith(actualClosure[child]); }
                direct.Add(pair.Key, children);
            }
            return direct;
        }
    }
}
