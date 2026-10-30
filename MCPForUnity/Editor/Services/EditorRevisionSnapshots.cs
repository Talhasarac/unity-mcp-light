using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    // Bounded, session-bound observations. Domain reload intentionally invalidates all tokens.
    // Fingerprints include live serialized values AND disk contents, catching human/file edits
    // as well as MCP writes. They do not claim to snapshot arbitrary runtime/static state.
    internal static class EditorRevisionSnapshots
    {
        private sealed class Snapshot
        {
            public string Agent;
            public double Created;
            public string[] Resources;
            public string[] Fingerprints;
        }
        private static readonly Dictionary<string, Snapshot> snapshots = new();
        private static double Now => EditorApplication.timeSinceStartup;

        internal static JObject Capture(string agent, JArray resources)
        {
            EditorCommandAccess.ResolveResources(resources);
            foreach (var key in snapshots.Where(p => Now - p.Value.Created > 600).Select(p => p.Key).ToArray()) snapshots.Remove(key);
            if (snapshots.Count >= 128) snapshots.Remove(snapshots.OrderBy(p => p.Value.Created).First().Key);
            var selectors = resources.Values<string>().Distinct().ToArray();
            var snapshot = new Snapshot { Agent = agent, Created = Now, Resources = selectors,
                Fingerprints = selectors.Select(Fingerprint).ToArray() };
            string token = Guid.NewGuid().ToString("N");
            snapshots.Add(token, snapshot);
            return new JObject { ["success"] = true, ["data"] = new JObject {
                ["revision"] = token, ["resources"] = new JArray(selectors), ["expires_in_seconds"] = 600,
                ["instruction"] = "Inspect these resources now, then pass expected_revision to batch_execute. Include all read dependencies. Tokens are single-use and expire on reload." } };
        }

        internal static string[] GetReads(string agent, string token, out JObject error)
        {
            error = null;
            if (!snapshots.TryGetValue(token, out var snapshot) || snapshot.Agent != agent || Now - snapshot.Created > 600)
            {
                error = Conflict("Snapshot expired or belongs to another session. Take a new snapshot and re-read.");
                return Array.Empty<string>();
            }
            try { return snapshot.Resources.Select(EditorCommandAccess.ResolveResource).ToArray(); }
            catch (Exception)
            {
                error = Conflict("A snapshotted resource no longer exists. Take a new snapshot and re-read.");
                return Array.Empty<string>();
            }
        }

        internal static JObject Consume(string agent, string token)
        {
            if (!snapshots.TryGetValue(token, out var snapshot) || snapshot.Agent != agent || Now - snapshot.Created > 600)
                return Conflict("Snapshot missing, expired, or from another session. Take a new snapshot and re-read before editing.");
            snapshots.Remove(token);
            try
            {
                if (!snapshot.Resources.Select(Fingerprint).SequenceEqual(snapshot.Fingerprints))
                    return Conflict("A snapshotted object, asset, or dependency changed. No batch commands ran; take a new snapshot and re-read.");
            }
            catch (Exception)
            {
                return Conflict("A snapshotted resource is no longer readable. No batch commands ran; take a new snapshot and re-read.");
            }
            return null;
        }

        private static JObject Conflict(string message) => new() { ["success"] = false, ["error"] = "conflict", ["code"] = "conflict", ["message"] = message };

        private static string Fingerprint(string selector)
        {
            var text = new StringBuilder(EditorCommandAccess.ResolveResource(selector));
            if (selector.StartsWith("object:", StringComparison.Ordinal))
            {
                var go = GameObjectLookup.FindById(int.Parse(selector.Substring(7)));
                // Include parent transforms: moving an ancestor changes the target's world pose.
                for (var parent = go.transform.parent; parent != null; parent = parent.parent)
                    AppendObject(text, parent);
                foreach (var transform in go.GetComponentsInChildren<Transform>(true))
                {
                    AppendObject(text, transform.gameObject);
                    foreach (var component in transform.GetComponents<Component>()) AppendObject(text, component);
                }
            }
            else
            {
                string path = EditorCommandAccess.AssetPath(selector.Substring(6));
                if (Directory.Exists(path)) throw new ArgumentException("Snapshot exact assets, not folders.");
                AppendFile(text, path);
                AppendFile(text, path + ".meta");
                // Importer settings/dependencies and unsaved in-memory asset edits also matter.
                text.Append(AssetDatabase.GetAssetDependencyHash(path).ToString());
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) AppendObject(text, asset);
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root != null)
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        AppendObject(text, transform.gameObject);
                        foreach (var component in transform.GetComponents<Component>()) AppendObject(text, component);
                    }
            }
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        private static void AppendObject(StringBuilder text, UnityEngine.Object obj)
        {
            if (obj == null) { text.Append("|missing|"); return; }
            text.Append('|').Append(obj.GetInstanceIDCompat()).Append(':').Append(EditorJsonUtility.ToJson(obj));
        }
        private static void AppendFile(StringBuilder text, string path)
        {
            if (!File.Exists(path)) { text.Append("|absent|"); return; }
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            text.Append('|').Append(Convert.ToBase64String(hash.ComputeHash(stream)));
        }
    }
}
